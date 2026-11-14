# 📁 DiskExplorer

![DiskExplorer Önizleme](app_preview.png)

**DiskExplorer**, Windows işletim sistemine sahip bilgisayarlarda seçtiğiniz sürücülerdeki (C:, D:, harici diskler vb.) tüm klasör ve dosyaları hızlı bir şekilde tarayarak listeleyen, C# ve WPF (XAML) teknolojileriyle geliştirilmiş hafif ve kullanışlı bir masaüstü uygulamasıdır.

---

## ✨ Özellikler

* **Çoklu Sürücü Seçimi:** Bilgisayarınıza bağlı yerel ve harici sürücülerden istediklerinizi işaretleyerek aynı anda tarama başlatabilirsiniz.
* **Kapsamlı Dosya Listeleme:** Seçilen sürücülerdeki tüm alt klasörleri ve dosyaları derinlemesine tarar ve düzenli bir liste halinde sunar.
* **Hızlı ve Hafif Yapı:** Modern C# ve .NET altyapısı sayesinde sistem kaynaklarını yormadan yüksek performansla çalışır.
* **Kullanıcı Dostu Arayüz:** Karmaşadan uzak, sade ve anlaşılır WPF masaüstü arayüzü ile kolay kullanım sağlar.

---

## 🛠️ Gereksinimler

* **İşletim Sistemi:** Windows 10 veya Windows 11
* **Çalışma Zamanı (Runtime):** .NET Desktop Runtime (Uygulamanın derlendiği sürüme uygun sürüm)

---

## 🚀 Kurulum ve Kullanım

### Hazır Sürümü Çalıştırma (Son Kullanıcılar İçin)
1. Sağ taraftaki **Releases** (Sürümler) bölümünden en güncel `.zip` dosyasını bilgisayarınıza indirin.
2. İndirdiğiniz arşiv dosyasını bir klasöre çıkartın.
3. Klasör içerisindeki **`DiskExplorer.exe`** dosyasına çift tıklayarak programı başlatın.
4. Listelemek istediğiniz sürücüleri işaretleyin ve tarama işlemini başlatın.

### Kaynak Koddan Derleme (Geliştiriciler İçin)
Projeyi kendi bilgisayarınızda derlemek için Visual Studio (2022 veya üzeri) ve .NET SDK kurulu olmalıdır:

```bash
# Depoyu bilgisayarınıza klonlayın
git clone https://github.com/ozcakirh/DiskExplorer.git

# Proje dizinine girin
cd DiskExplorer

# Projeyi derleyin ve çalıştırın
dotnet run
```

---

## 📂 Proje Yapısı

* `App.xaml` / `App.xaml.cs`: Uygulama başlangıç ayarları ve genel kaynak tanımlamaları.
* `MainWindow.xaml` / `MainWindow.xaml.cs`: Ana kullanıcı arayüzü ve sürücü/dosya tarama mantığı.
* `Models.cs`: Dosya ve sürücü bilgilerini tutan veri modelleri.
* `app.ico` & `app_preview.png`: Uygulama ikonu ve arayüz önizleme görseli.

---

## 👨‍💻 Geliştirici

**Hüseyin ÖZÇAKIR**  
GitHub: [@ozcakirh](https://github.com/ozcakirh)