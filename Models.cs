using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Web;

namespace DiskExplorer
{
    // =====================================================================
    //  BOYUT FORMATLAMA:  0 B, 512 KB, 280 MB, 980 GB ...
    //  1 KB = 1024 B, 1 MB = 1024 KB, 1 GB = 1024 MB
    //  Hüseyin ÖZÇAKIR , Bursa 2026
    // =====================================================================
    public static class SizeFormatter
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Format(long bytes)
        {
            if (bytes < 1024) return bytes.ToString("N0") + " B";

            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString("0.##") + " " + Units[unit];
        }
    }

    // =====================================================================
    //  MIME TİPİ (uzantıya göre, sonuçlar önbelleğe alınır)
    // =====================================================================
    public static class MimeHelper
    {
        private const string Default = "application/octet-stream";

        private static readonly ConcurrentDictionary<string, string> Cache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <param name="extension">Nokta dahil uzantı (örn: ".pdf")</param>
        public static string Get(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return Default;

            return Cache.GetOrAdd(extension, ext =>
            {
                try { return MimeMapping.GetMimeMapping("file" + ext); }
                catch { return Default; }
            });
        }
    }

    // =====================================================================
    //  DOSYA KATEGORİSİ (liste satırlarını renklendirmek için)
    // =====================================================================
    public static class FileCategory
    {
        public const string Directory = "Dir";    // Sarı
        public const string App = "App";          // Mavi
        public const string Dll = "Dll";          // Mor
        public const string Other = "Other";      // Standart

        // "Uygulama" sayılacak uzantılar. İsterseniz buraya ekleme/çıkarma yapabilirsiniz.
        private static readonly HashSet<string> AppExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".exe", ".msi", ".com", ".scr", ".bat", ".cmd"
            };

        public static string ForExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return Other;
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return Dll;
            if (AppExtensions.Contains(extension)) return App;
            return Other;
        }
    }

    // =====================================================================
    //  DOSYA SİSTEMİ YARDIMCISI
    // =====================================================================
    public static class FsHelper
    {
        /// <summary>
        /// Bir klasörün içeriğini (dosya + alt klasör) güvenli şekilde listeler.
        /// Erişim izni yoksa veya klasör okunamıyorsa exception fırlatmaz, sessizce boş döner.
        /// (.NET Framework 4.8'de EnumerationOptions.IgnoreInaccessible bulunmadığı için
        ///  numaralandırıcı elle yönetilir.)
        /// </summary>
        public static IEnumerable<FileSystemInfo> SafeEnumerate(string path, CancellationToken ct)
        {
            IEnumerator<FileSystemInfo> en = null;
            try
            {
                en = new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator();
            }
            catch
            {
                // Geçersiz yol / erişim yok: en = null kalır
            }

            if (en == null) yield break;

            using (en)
            {
                while (true)
                {
                    FileSystemInfo current;
                    try
                    {
                        if (!en.MoveNext()) break;
                        current = en.Current;
                    }
                    catch
                    {
                        break; // Numaralandırma sırasında erişim hatası: bu klasörü bırak
                    }

                    if (ct.IsCancellationRequested) yield break;
                    yield return current;
                }
            }
        }

        /// <summary>Junction / symlink klasörü mü? (Sonsuz döngü ve çift sayımı önlemek için atlanır.)</summary>
        public static bool IsReparsePoint(FileSystemInfo info)
        {
            try { return (info.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        /// <summary>Kompakt öznitelik gösterimi: R=ReadOnly H=Hidden S=System A=Archive C=Compressed E=Encrypted</summary>
        public static string AttributeString(FileAttributes a)
        {
            var chars = new char[6];
            int n = 0;
            if ((a & FileAttributes.ReadOnly) != 0) chars[n++] = 'R';
            if ((a & FileAttributes.Hidden) != 0) chars[n++] = 'H';
            if ((a & FileAttributes.System) != 0) chars[n++] = 'S';
            if ((a & FileAttributes.Archive) != 0) chars[n++] = 'A';
            if ((a & FileAttributes.Compressed) != 0) chars[n++] = 'C';
            if ((a & FileAttributes.Encrypted) != 0) chars[n++] = 'E';
            return new string(chars, 0, n);
        }
    }

    // =====================================================================
    //  SÜRÜCÜ LİSTESİ ÖĞESİ
    // =====================================================================
    public sealed class DriveItem
    {
        public string Root { get; set; }        // "C:\"
        public string Display { get; set; }     // Ekranda görünen metin
        public bool IsChecked { get; set; }     // CheckBox (TwoWay)
    }

    // =====================================================================
    //  AĞAÇ DÜĞÜMÜ (klasör) - tarama sonucu bellekte tutulur
    // =====================================================================
    public sealed class DirNode : INotifyPropertyChanged
    {
        // Çocuğu olmayan düğümler aynı boş listeyi paylaşır (bellek tasarrufu)
        private static readonly List<DirNode> NoChildren = new List<DirNode>();

        private bool _isExpanded;
        private bool _isSelected;

        public DirNode(string name, string fullPath, bool isDrive)
        {
            Name = name;
            FullPath = fullPath;
            IsDrive = isDrive;
            Children = NoChildren;
        }

        public string Name { get; private set; }
        public string FullPath { get; private set; }
        public bool IsDrive { get; private set; }

        /// <summary>Bu klasör ve tüm alt klasörlerindeki toplam dosya boyutu.</summary>
        public long TotalSize { get; set; }

        /// <summary>Bu klasör ve tüm alt klasörlerindeki toplam dosya sayısı.</summary>
        public long TotalFiles { get; set; }

        /// <summary>Üst klasörün toplam boyutu içindeki payı (0-100). Arka plandaki bar için.</summary>
        public double Percent { get; set; }

        public List<DirNode> Children { get; set; }

        // ---- Ekranda görünen metinler ----
        public string Icon { get { return IsDrive ? "\U0001F4BD" : "\U0001F4C1"; } }
        public string SizeText { get { return SizeFormatter.Format(TotalSize); } }
        public string FilesText { get { return "- " + TotalFiles.ToString("N0") + " File"; } }

        // ---- TreeViewItem ile TwoWay bağlanan durumlar ----
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { if (_isExpanded != value) { _isExpanded = value; Raise("IsExpanded"); } }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { if (_isSelected != value) { _isSelected = value; Raise("IsSelected"); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    // =====================================================================
    //  SAĞ LİSTE SATIRI (dosya veya klasör)
    // =====================================================================
    public sealed class FileRow
    {
        private bool _hasSize = true;

        public string Name { get; private set; }
        public string Extension { get; private set; }
        public string FullPath { get; private set; }
        public string MimeType { get; private set; }
        public string Attributes { get; private set; }
        public bool IsDirectory { get; private set; }

        /// <summary>Renklendirme kategorisi: Dir / App / Dll / Other</summary>
        public string Category { get; private set; }
        public long SizeBytes { get; private set; }
        public DateTime Created { get; private set; }
        public DateTime Modified { get; private set; }
        public DateTime Accessed { get; private set; }

        public string Icon { get { return IsDirectory ? "\U0001F4C1" : "\U0001F4C4"; } }
        public string SizeText { get { return _hasSize ? SizeFormatter.Format(SizeBytes) : "-"; } }

        public static FileRow ForFile(FileInfo fi)
        {
            string ext = fi.Extension;
            return new FileRow
            {
                Name = fi.Name,
                Extension = ext,
                FullPath = fi.FullName,
                MimeType = MimeHelper.Get(ext),
                Category = FileCategory.ForExtension(ext),
                Attributes = FsHelper.AttributeString(fi.Attributes),
                IsDirectory = false,
                SizeBytes = fi.Length,
                Created = fi.CreationTime,
                Modified = fi.LastWriteTime,
                Accessed = fi.LastAccessTime
            };
        }

        public static FileRow ForDirectory(DirectoryInfo di, long scannedSize, bool hasSize)
        {
            return new FileRow
            {
                Name = di.Name,
                Extension = "",
                FullPath = di.FullName,
                MimeType = "inode/directory",
                Category = FileCategory.Directory,
                Attributes = FsHelper.AttributeString(di.Attributes),
                IsDirectory = true,
                SizeBytes = scannedSize,
                _hasSize = hasSize,
                Created = di.CreationTime,
                Modified = di.LastWriteTime,
                Accessed = di.LastAccessTime
            };
        }
    }
}
