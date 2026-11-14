using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiskExplorer
{
    /// <summary>
    /// Disk Gezgini ana penceresi.
    ///
    /// Akış:
    ///  1) Seçilen her sürücü arka planda taranır; her klasör için toplam boyut ve dosya sayısı hesaplanır.
    ///  2) Sonuç (yalnızca klasör ağacı) bellekte tutulur ve sol taraftaki TreeView'a bağlanır.
    ///  3) Bir klasör seçilince, o klasörün içeriği (dosya + alt klasör) diskten okunup sağdaki listeye yazılır.
    ///     Böylece milyonlarca dosya için bellekte tek tek dosya kaydı tutulmaz.
    ///     Hüseyin ÖZÇAKIR , 2026 Bursa
    /// </summary>
    public partial class MainWindow : Window
    {
        // ---------------- Ayarlar ----------------

        /// <summary>
        /// Kök seviyesinden kaç kademe derinliğe kadar klasörlerin paralel taranacağı.
        /// SSD/NVMe için 2 iyidir. Klasik HDD kullanıyorsanız 0 yaparak paralelliği kapatabilirsiniz.
        /// </summary>
        private const int ParallelDepth = 2;

        /// <summary>"Alt klasörleri de listele" modunda sağ listeye alınacak en fazla satır sayısı.</summary>
        private const int MaxRecursiveRows = 500000;

        private static readonly ParallelOptions ParallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount))
        };

        // Sağ liste kolon başlığı -> sıralama özelliği eşlemesi
        private static readonly Dictionary<string, string> SortMap = new Dictionary<string, string>
        {
            { "File Name", "Name" },
            { "Extension", "Extension" },
            { "Size", "SizeBytes" },
            { "Create Date", "Created" },
            { "Last Date", "Modified" },
            { "Last Access", "Accessed" },
            { "Mime Type", "MimeType" },
            { "Attributes", "Attributes" },
            { "Full Path", "FullPath" }
        };

        // ---------------- Bağlanan koleksiyonlar ----------------
        public ObservableCollection<DriveItem> Drives { get; } = new ObservableCollection<DriveItem>();
        public ObservableCollection<DirNode> Roots { get; } = new ObservableCollection<DirNode>();

        // ---------------- Durum alanları ----------------
        private CancellationTokenSource _scanCts;
        private CancellationTokenSource _loadCts;
        private DirNode _selectedNode;

        // Tarama ilerleme sayaçları (arka plan thread'leri Interlocked ile günceller)
        private long _scanFiles, _scanDirs, _scanBytes;
        private string _currentDrive = "";
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly DispatcherTimer _statusTimer;

        // Sağ liste sıralama durumu
        private string _sortProp = "Name";
        private ListSortDirection _sortDir = ListSortDirection.Ascending;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Durum çubuğunu saniyede ~3 kez güncelle (her dosyada UI'a dokunmayız)
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _statusTimer.Tick += (s, e) => UpdateScanStatus();

            LoadDrives();
        }

        // =====================================================================
        //  SÜRÜCÜ LİSTESİ
        // =====================================================================

        private void LoadDrives()
        {
            Drives.Clear();
            bool firstFixedChecked = false;

            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue; // Boş CD/DVD, bağlı olmayan sürücüler

                    string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Yerel Disk" : d.VolumeLabel;
                    long used = d.TotalSize - d.TotalFreeSpace;

                    var item = new DriveItem
                    {
                        Root = d.RootDirectory.FullName,
                        Display = string.Format("{0}  {1}  [{2}]  {3} / {4}",
                            d.Name, label, d.DriveType,
                            SizeFormatter.Format(used), SizeFormatter.Format(d.TotalSize))
                    };

                    // Varsayılan olarak ilk sabit diski işaretle
                    if (!firstFixedChecked && d.DriveType == DriveType.Fixed)
                    {
                        item.IsChecked = true;
                        firstFixedChecked = true;
                    }
                    Drives.Add(item);
                }
                catch
                {
                    // Okunamayan sürücüyü atla
                }
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadDrives();
        }

        // =====================================================================
        //  TARAMA BAŞLAT / DURDUR
        // =====================================================================

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            // Tarama sürüyorsa buton "Durdur" işlevi görür
            if (_scanCts != null)
            {
                _scanCts.Cancel();
                BtnScan.IsEnabled = false;
                return;
            }

            List<DriveItem> selected = Drives.Where(d => d.IsChecked).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Lütfen en az bir sürücü seçin.", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ---- Hazırlık ----
            if (_loadCts != null) _loadCts.Cancel();
            Roots.Clear();
            ListFiles.ItemsSource = null;
            _selectedNode = null;
            TxtPath.Text = "Bir klasör seçin";
            TxtListStatus.Text = "";

            Interlocked.Exchange(ref _scanFiles, 0);
            Interlocked.Exchange(ref _scanDirs, 0);
            Interlocked.Exchange(ref _scanBytes, 0);

            _scanCts = new CancellationTokenSource();
            CancellationToken ct = _scanCts.Token;
            SetScanUi(true);
            _stopwatch.Restart();
            _statusTimer.Start();

            bool cancelled = false;
            try
            {
                foreach (DriveItem drive in selected)
                {
                    _currentDrive = drive.Root;
                    string root = drive.Root;

                    // Taramayı thread pool'da yap; UI thread'i serbest kalır
                    DirNode node = await Task.Run(() => ScanDrive(root, ct));

                    if (ct.IsCancellationRequested) { cancelled = true; break; }

                    Roots.Add(node);

                    // İlk sürücüyü otomatik aç ve seç
                    if (Roots.Count == 1)
                    {
                        node.IsExpanded = true;
                        node.IsSelected = true;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Tarama sırasında hata oluştu:\n" + ex.Message, "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // ---- Bitiş ----
            _statusTimer.Stop();
            _stopwatch.Stop();
            _scanCts.Dispose();
            _scanCts = null;
            SetScanUi(false);

            TxtStatus.Text = string.Format("{0}  |  {1:N0} dosya  |  {2:N0} klasör  |  {3}  |  Süre: {4}",
                cancelled ? "Tarama durduruldu" : "Tarama tamamlandı",
                Interlocked.Read(ref _scanFiles),
                Interlocked.Read(ref _scanDirs),
                SizeFormatter.Format(Interlocked.Read(ref _scanBytes)),
                _stopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
        }

        private void SetScanUi(bool scanning)
        {
            BtnScan.Content = scanning ? "■ Taramayı Durdur" : "▶ Taramayı Başlat";
            BtnScan.IsEnabled = true;
            BtnRefresh.IsEnabled = !scanning;
            DriveList.IsEnabled = !scanning;
            PrgScan.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateScanStatus()
        {
            TxtStatus.Text = string.Format("Taranıyor: {0}  |  {1:N0} dosya  |  {2:N0} klasör  |  {3}  |  Süre: {4}",
                _currentDrive,
                Interlocked.Read(ref _scanFiles),
                Interlocked.Read(ref _scanDirs),
                SizeFormatter.Format(Interlocked.Read(ref _scanBytes)),
                _stopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
        }

        // =====================================================================
        //  TARAMA MOTORU (arka plan thread'lerinde çalışır)
        // =====================================================================

        private DirNode ScanDrive(string rootPath, CancellationToken ct)
        {
            var root = new DirNode(rootPath, rootPath, true);
            ScanNode(root, 0, ct);
            root.Percent = 100;
            return root;
        }

        /// <summary>
        /// Bir klasörü tarar: kendi dosyalarını toplar, alt klasörleri düğüm olarak ekler,
        /// alt klasörler bittikten sonra toplam boyut / dosya sayısını ve pay yüzdelerini hesaplar.
        /// </summary>
        private void ScanNode(DirNode node, int depth, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            var subDirs = new List<DirNode>();
            long ownSize = 0;
            long ownFiles = 0;

            foreach (FileSystemInfo fsi in FsHelper.SafeEnumerate(node.FullPath, ct))
            {
                try
                {
                    var di = fsi as DirectoryInfo;
                    if (di != null)
                    {
                        // Junction/symlink klasörlerine girme (döngü ve çift sayım riski)
                        if (FsHelper.IsReparsePoint(di)) continue;
                        subDirs.Add(new DirNode(di.Name, di.FullName, false));
                    }
                    else
                    {
                        ownSize += ((FileInfo)fsi).Length;
                        ownFiles++;
                    }
                }
                catch
                {
                    // Tek bir girdi okunamadıysa devam et
                }
            }

            Interlocked.Add(ref _scanFiles, ownFiles);
            Interlocked.Add(ref _scanBytes, ownSize);
            Interlocked.Increment(ref _scanDirs);

            // Alt klasörleri tara (üst kademelerde paralel, derinde sıralı)
            if (depth < ParallelDepth && subDirs.Count > 1)
            {
                Parallel.ForEach(subDirs, ParallelOpts, d => ScanNode(d, depth + 1, ct));
            }
            else
            {
                foreach (DirNode d in subDirs) ScanNode(d, depth + 1, ct);
            }

            // Toplamları hesapla
            long totalSize = ownSize;
            long totalFiles = ownFiles;
            foreach (DirNode d in subDirs)
            {
                totalSize += d.TotalSize;
                totalFiles += d.TotalFiles;
            }

            // Büyükten küçüğe sırala (boş ağırlıklar alfabetik)
            subDirs.Sort((a, b) =>
            {
                int c = b.TotalSize.CompareTo(a.TotalSize);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            foreach (DirNode d in subDirs)
                d.Percent = totalSize > 0 ? d.TotalSize * 100.0 / totalSize : 0;

            node.TotalSize = totalSize;
            node.TotalFiles = totalFiles;
            node.Children = subDirs;
        }

        // =====================================================================
        //  AĞAÇ OLAYLARI
        // =====================================================================

        private async void TreeFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            _selectedNode = e.NewValue as DirNode;
            if (_selectedNode != null)
                await LoadFolderAsync(_selectedNode);
        }

        /// <summary>Seçilen satırı görünür alana kaydır (programatik seçimlerde de işe yarar).</summary>
        private void TreeItem_Selected(object sender, RoutedEventArgs e)
        {
            var item = sender as TreeViewItem;
            if (item != null) item.BringIntoView();
            e.Handled = true; // Üst düğümlerin de tetiklenmesini engelle
        }

        /// <summary>Sağ tıklanan düğümü seç (WPF varsayılan olarak seçmez).</summary>
        private void Tree_PreviewRightClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null && !(d is TreeViewItem))
            {
                d = (d is Visual) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }

            var item = d as TreeViewItem;
            if (item != null)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void TreeMenuOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedNode == null) return;
            StartProcess("explorer.exe", "\"" + _selectedNode.FullPath + "\"");
        }

        private void TreeMenuCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedNode == null) return;
            CopyText(_selectedNode.FullPath);
        }

        // =====================================================================
        //  SAĞ LİSTE: SEÇİLEN KLASÖRÜN İÇERİĞİ
        // =====================================================================

        private void ChkRecursive_Changed(object sender, RoutedEventArgs e)
        {
            if (_selectedNode != null)
            {
                var ignored = LoadFolderAsync(_selectedNode);
            }
        }

        private async Task LoadFolderAsync(DirNode node)
        {
            // Önceki yükleme sürüyorsa iptal et (hızlı klasör geçişlerinde)
            if (_loadCts != null) _loadCts.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            CancellationToken ct = cts.Token;

            bool recursive = ChkRecursive.IsChecked == true;
            TxtPath.Text = node.FullPath;
            TxtListStatus.Text = "Yükleniyor...";

            List<FileRow> rows;
            try
            {
                rows = await Task.Run(() => BuildRows(node, recursive, ct));
            }
            catch (Exception ex)
            {
                TxtListStatus.Text = "Hata: " + ex.Message;
                return;
            }

            // Bu arada başka bir klasör seçildiyse bu sonucu at
            if (ct.IsCancellationRequested) return;

            ListFiles.ItemsSource = rows;
            ApplySort();

            int dirCount = rows.Count(r => r.IsDirectory);
            int fileCount = rows.Count - dirCount;
            long fileBytes = rows.Where(r => !r.IsDirectory).Sum(r => r.SizeBytes);

            string info = string.Format("{0:N0} klasör, {1:N0} dosya  |  Listelenen dosya boyutu: {2}",
                dirCount, fileCount, SizeFormatter.Format(fileBytes));
            if (recursive && rows.Count >= MaxRecursiveRows)
                info += string.Format("  |  (ilk {0:N0} kayıtla sınırlandı)", MaxRecursiveRows);
            TxtListStatus.Text = info;
        }

        /// <summary>Arka planda sağ liste satırlarını üretir.</summary>
        private static List<FileRow> BuildRows(DirNode node, bool recursive, CancellationToken ct)
        {
            var rows = new List<FileRow>();

            if (!recursive)
            {
                // Alt klasörlerin boyutu tarama sonucundan gelir
                var map = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
                foreach (DirNode c in node.Children) map[c.Name] = c;

                foreach (FileSystemInfo fsi in FsHelper.SafeEnumerate(node.FullPath, ct))
                {
                    FileRow row = CreateRow(fsi, map);
                    if (row != null) rows.Add(row);
                }
            }
            else
            {
                // Alt klasörler dahil tüm dosyalar (yalnızca dosyalar listelenir)
                var stack = new Stack<string>();
                stack.Push(node.FullPath);

                while (stack.Count > 0 && !ct.IsCancellationRequested)
                {
                    string current = stack.Pop();
                    foreach (FileSystemInfo fsi in FsHelper.SafeEnumerate(current, ct))
                    {
                        var di = fsi as DirectoryInfo;
                        if (di != null)
                        {
                            if (!FsHelper.IsReparsePoint(di)) stack.Push(di.FullName);
                            continue;
                        }

                        FileRow row = CreateRow(fsi, null);
                        if (row != null) rows.Add(row);
                        if (rows.Count >= MaxRecursiveRows) return rows;
                    }
                }
            }
            return rows;
        }

        private static FileRow CreateRow(FileSystemInfo fsi, Dictionary<string, DirNode> map)
        {
            try
            {
                var di = fsi as DirectoryInfo;
                if (di != null)
                {
                    DirNode scanned = null;
                    bool has = map != null && map.TryGetValue(di.Name, out scanned);
                    return FileRow.ForDirectory(di, has ? scanned.TotalSize : 0, has);
                }
                return FileRow.ForFile((FileInfo)fsi);
            }
            catch
            {
                return null; // Okunamayan girdiyi atla
            }
        }

        // ---------------- Sıralama ----------------

        private void ListHeader_Click(object sender, RoutedEventArgs e)
        {
            var header = e.OriginalSource as GridViewColumnHeader;
            if (header == null || header.Role == GridViewColumnHeaderRole.Padding) return;

            string title = header.Column != null ? header.Column.Header as string : null;
            string prop;
            if (title == null || !SortMap.TryGetValue(title, out prop)) return;

            if (prop == _sortProp)
            {
                _sortDir = _sortDir == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }
            else
            {
                _sortProp = prop;
                _sortDir = ListSortDirection.Ascending;
            }
            ApplySort();
        }

        /// <summary>Klasörler her zaman üstte; sonra seçili kolona göre sırala.</summary>
        private void ApplySort()
        {
            if (ListFiles.ItemsSource == null) return;
            ICollectionView view = CollectionViewSource.GetDefaultView(ListFiles.ItemsSource);
            if (view == null) return;

            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription("IsDirectory", ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(_sortProp, _sortDir));
            }
        }

        // ---------------- Çift tıklama ----------------

        private void ListFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject src = e.OriginalSource as DependencyObject;
            if (src == null) return;

            var item = ItemsControl.ContainerFromElement(ListFiles, src) as ListViewItem;
            if (item == null) return; // Başlık veya boş alan

            var row = item.Content as FileRow;
            if (row == null) return;

            if (row.IsDirectory)
            {
                // Klasör: ağaçta o klasöre git
                DirNode child = _selectedNode == null ? null :
                    _selectedNode.Children.FirstOrDefault(c =>
                        string.Equals(c.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase));

                if (child != null)
                {
                    _selectedNode.IsExpanded = true;
                    child.IsSelected = true;
                }
                else
                {
                    StartProcess("explorer.exe", "\"" + row.FullPath + "\"");
                }
            }
            else
            {
                // Dosya: varsayılan programla aç
                OpenWithShell(row.FullPath);
            }
        }

        // ---------------- Sağ tık menüsü (liste) ----------------

        private void ListMenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var row = ListFiles.SelectedItem as FileRow;
            if (row == null) return;
            if (row.IsDirectory) StartProcess("explorer.exe", "\"" + row.FullPath + "\"");
            else OpenWithShell(row.FullPath);
        }

        private void ListMenuShow_Click(object sender, RoutedEventArgs e)
        {
            var row = ListFiles.SelectedItem as FileRow;
            if (row == null) return;
            StartProcess("explorer.exe", "/select,\"" + row.FullPath + "\"");
        }

        private void ListMenuCopy_Click(object sender, RoutedEventArgs e)
        {
            string text = string.Join(Environment.NewLine,
                ListFiles.SelectedItems.Cast<FileRow>().Select(r => r.FullPath));
            if (text.Length > 0) CopyText(text);
        }

        // =====================================================================
        //  YARDIMCILAR
        // =====================================================================

        private static void OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Açılamadı:\n" + path + "\n\n" + ex.Message, "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void StartProcess(string file, string args)
        {
            try { Process.Start(file, args); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void CopyText(string text)
        {
            try { Clipboard.SetText(text); }
            catch { /* Pano başka uygulama tarafından kilitli olabilir */ }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Arka plan işlemlerini güvenle durdur
            if (_scanCts != null) _scanCts.Cancel();
            if (_loadCts != null) _loadCts.Cancel();
        }
    }
}
