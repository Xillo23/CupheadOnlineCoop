// Cuphead Online Co-op installer: one .exe with the mod files embedded (payload.zip resource).
// Finds Cuphead (Steam libraries, GOG, common folders) or lets the user pick the folder.
// Records every file it creates in CupheadOnlineCoop-install.txt so Uninstall removes only those.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Cuphead Online Co-op Installer")]
[assembly: AssemblyProduct("Cuphead Online Co-op (unofficial fan mod)")]
[assembly: AssemblyVersion("0.2.1.0")]

namespace CupheadOnlineCoopInstaller
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Optional: --lang en (default = Windows display language).
            int i = Array.IndexOf(args, "--lang");
            if (i >= 0 && i + 1 < args.Length)
                try { System.Threading.Thread.CurrentThread.CurrentUICulture = new CultureInfo(args[i + 1]); } catch { }
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }

    static class Tx
    {
        // en fr it de es ko ru pl pt ja zh
        static readonly string[] Langs = { "en", "fr", "it", "de", "es", "ko", "ru", "pl", "pt", "ja", "zh" };
        static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]>
        {
            { "title", new[] { "Cuphead Online Co-op - Installer", "Cuphead Coop en ligne - Installation", "Cuphead Co-op Online - Installazione", "Cuphead Online-Koop - Installation",
                "Cuphead Cooperativo Online - Instalador", "Cuphead 온라인 협동 - 설치", "Cuphead Онлайн-кооп - Установка", "Cuphead Kooperacja Online - Instalator",
                "Cuphead Cooperativo Online - Instalador", "Cuphead オンライン協力プレイ - インストーラー", "Cuphead 在线合作 - 安装程序" } },
            { "pick", new[] { "Cuphead folder (where Cuphead.exe is):", "Dossier de Cuphead (contenant Cuphead.exe) :", "Cartella di Cuphead (dove c'è Cuphead.exe):",
                "Cuphead-Ordner (mit Cuphead.exe):", "Carpeta de Cuphead (donde está Cuphead.exe):", "Cuphead 폴더 (Cuphead.exe가 있는 곳):", "Папка Cuphead (где находится Cuphead.exe):",
                "Folder Cuphead (z plikiem Cuphead.exe):", "Pasta do Cuphead (onde está o Cuphead.exe):", "Cupheadのフォルダー（Cuphead.exeがある場所）：", "Cuphead 文件夹（Cuphead.exe 所在位置）：" } },
            { "browse", new[] { "Browse...", "Parcourir...", "Sfoglia...", "Durchsuchen...", "Examinar...", "찾아보기...", "Обзор...", "Przeglądaj...", "Procurar...", "参照...", "浏览..." } },
            { "install", new[] { "Install", "Installer", "Installa", "Installieren", "Instalar", "설치", "Установить", "Zainstaluj", "Instalar", "インストール", "安装" } },
            { "uninstall", new[] { "Uninstall", "Désinstaller", "Disinstalla", "Deinstallieren", "Desinstalar", "제거", "Удалить", "Odinstaluj", "Desinstalar", "アンインストール", "卸载" } },
            { "notfound", new[] { "Cuphead.exe was not found in this folder.", "Cuphead.exe est introuvable dans ce dossier.", "Cuphead.exe non trovato in questa cartella.",
                "Cuphead.exe wurde in diesem Ordner nicht gefunden.", "No se encontró Cuphead.exe en esta carpeta.", "이 폴더에서 Cuphead.exe를 찾을 수 없습니다.", "В этой папке нет Cuphead.exe.",
                "Nie znaleziono Cuphead.exe w tym folderze.", "Cuphead.exe não foi encontrado nesta pasta.", "このフォルダーにCuphead.exeが見つかりません。", "此文件夹中未找到 Cuphead.exe。" } },
            { "running", new[] { "Close Cuphead first.", "Fermez d'abord Cuphead.", "Chiudi prima Cuphead.", "Schließe zuerst Cuphead.", "Cierra Cuphead primero.", "먼저 Cuphead를 종료하세요.",
                "Сначала закройте Cuphead.", "Najpierw zamknij Cuphead.", "Feche o Cuphead primeiro.", "先にCupheadを終了してください。", "请先关闭 Cuphead。" } },
            { "done", new[] { "Installed! Start Cuphead as usual: ONLINE CO-OP is in the main menu.", "Installé ! Lancez Cuphead normalement : COOP EN LIGNE est dans le menu principal.",
                "Installato! Avvia Cuphead come sempre: CO-OP ONLINE è nel menu principale.", "Installiert! Starte Cuphead wie gewohnt: ONLINE-KOOP ist im Hauptmenü.",
                "¡Instalado! Inicia Cuphead como siempre: COOPERATIVO ONLINE está en el menú principal.", "설치 완료! 평소처럼 Cuphead를 실행하세요: 메인 메뉴에 온라인 협동이 있습니다.",
                "Установлено! Запустите Cuphead как обычно: ОНЛАЙН-КООП есть в главном меню.", "Zainstalowano! Uruchom Cuphead jak zwykle: KOOPERACJA ONLINE jest w menu głównym.",
                "Instalado! Inicie o Cuphead normalmente: COOPERATIVO ONLINE está no menu principal.", "インストール完了！いつも通りCupheadを起動してください。メインメニューにオンライン協力プレイがあります。",
                "安装完成！照常启动 Cuphead：主菜单中会出现“在线合作”。" } },
            { "kept", new[] { "BepInEx was already installed: only the mod was added.", "BepInEx était déjà installé : seul le mod a été ajouté.", "BepInEx era già installato: è stata aggiunta solo la mod.",
                "BepInEx war bereits installiert: nur die Mod wurde hinzugefügt.", "BepInEx ya estaba instalado: solo se añadió el mod.", "BepInEx가 이미 설치되어 있어 모드만 추가했습니다.",
                "BepInEx уже был установлен: добавлен только мод.", "BepInEx był już zainstalowany: dodano tylko moda.", "O BepInEx já estava instalado: só o mod foi adicionado.",
                "BepInExは既にインストール済みのため、MODのみ追加しました。", "已安装 BepInEx：仅添加了本模组。" } },
            { "removed", new[] { "Uninstalled.", "Désinstallé.", "Disinstallato.", "Deinstalliert.", "Desinstalado.", "제거되었습니다.", "Удалено.", "Odinstalowano.", "Desinstalado.", "アンインストールしました。", "已卸载。" } },
            { "error", new[] { "Error: ", "Erreur : ", "Errore: ", "Fehler: ", "Error: ", "오류: ", "Ошибка: ", "Błąd: ", "Erro: ", "エラー：", "错误：" } },
            { "support", new[] { "This mod is free. If you enjoy it, a small donation helps me keep improving it ❤",
                "Ce mod est gratuit. Si vous l'aimez, un petit don m'aide à continuer à l'améliorer ❤",
                "Questa mod è gratuita. Se ti piace, una piccola donazione mi aiuta a continuare a migliorarla ❤",
                "Diese Mod ist kostenlos. Wenn sie dir gefällt, hilft mir eine kleine Spende, sie weiter zu verbessern ❤",
                "Este mod es gratis. Si te gusta, una pequeña donación me ayuda a seguir mejorándolo ❤",
                "이 모드는 무료입니다. 마음에 드셨다면 작은 후원이 개선을 계속하는 데 큰 힘이 됩니다 ❤",
                "Мод бесплатный. Если он вам нравится, небольшое пожертвование поможет мне его улучшать ❤",
                "Ten mod jest darmowy. Jeśli ci się podoba, mała darowizna pomoże mi go dalej ulepszać ❤",
                "Este mod é gratuito. Se você gostar, uma pequena doação me ajuda a continuar melhorando ❤",
                "このMODは無料です。気に入ったら、少額の寄付が今後の改善の励みになります ❤",
                "本模组免费。如果你喜欢，一点小小的捐助能帮助我继续改进它 ❤" } },
            { "donate", new[] { "☕ Support on Ko-fi", "☕ Soutenir sur Ko-fi", "☕ Sostienimi su Ko-fi", "☕ Auf Ko-fi unterstützen", "☕ Apoyar en Ko-fi",
                "☕ Ko-fi에서 후원하기", "☕ Поддержать на Ko-fi", "☕ Wesprzyj na Ko-fi", "☕ Apoiar no Ko-fi", "☕ Ko-fiで支援する", "☕ 在 Ko-fi 上支持" } },
            { "notice", new[] { "Unofficial fan mod, not affiliated with StudioMDHR. Both players need the same version.",
                "Mod de fan non officiel, sans lien avec StudioMDHR. Les deux joueurs ont besoin de la même version.",
                "Mod amatoriale non ufficiale, non affiliata a StudioMDHR. Servono la stessa versione su entrambi i PC.",
                "Inoffizielle Fan-Mod, nicht mit StudioMDHR verbunden. Beide Spieler brauchen dieselbe Version.",
                "Mod de fans no oficial, sin relación con StudioMDHR. Ambos jugadores necesitan la misma versión.",
                "StudioMDHR와 관련 없는 비공식 팬 모드입니다. 두 플레이어 모두 같은 버전이 필요합니다.",
                "Неофициальный фанатский мод, не связан со StudioMDHR. Обоим игрокам нужна одна версия.",
                "Nieoficjalny mod fanowski, niezwiązany ze StudioMDHR. Obaj gracze potrzebują tej samej wersji.",
                "Mod de fã não oficial, sem vínculo com a StudioMDHR. Os dois jogadores precisam da mesma versão.",
                "StudioMDHRとは無関係の非公式ファンMODです。両方のプレイヤーに同じバージョンが必要です。",
                "非官方粉丝模组，与 StudioMDHR 无关。两名玩家需要相同版本。" } },
        };

        static int index = -1;
        public static string Get(string key)
        {
            if (index < 0)
            {
                index = Array.IndexOf(Langs, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant());
                if (index < 0) index = 0;
            }
            return T[key][index];
        }
    }

    sealed class MainForm : Form
    {
        const string ManifestName = "CupheadOnlineCoop-install.txt";
        readonly TextBox path = new TextBox();
        readonly Label status = new Label();

        public MainForm()
        {
            Text = Tx.Get("title");
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 306);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label pick = new Label { Text = Tx.Get("pick"), Location = new Point(16, 16), AutoSize = true };
            path.SetBounds(16, 42, 420, 26);
            Button browse = new Button { Text = Tx.Get("browse") };
            browse.SetBounds(444, 40, 100, 30);
            browse.Click += delegate { Browse(); };
            Button install = new Button { Text = Tx.Get("install") };
            install.SetBounds(16, 84, 160, 36);
            install.Click += delegate { Run(Install); };
            Button uninstall = new Button { Text = Tx.Get("uninstall") };
            uninstall.SetBounds(186, 84, 160, 36);
            uninstall.Click += delegate { Run(Uninstall); };
            status.SetBounds(16, 130, 528, 56);
            Label notice = new Label { Text = Tx.Get("notice"), ForeColor = Color.DimGray };
            notice.SetBounds(16, 266, 450, 36);
            LinkLabel site = new LinkLabel { Text = "GitHub", TextAlign = ContentAlignment.TopRight };
            site.SetBounds(470, 266, 74, 20);
            site.LinkClicked += delegate { Open("https://github.com/Xillo23/CupheadOnlineCoop"); };

            // Donation box: visible, never blocking.
            Color coral = Color.FromArgb(255, 94, 91);
            Panel box = new Panel { BackColor = Color.FromArgb(255, 239, 238) };
            box.SetBounds(16, 192, 528, 64);
            Label ask = new Label { Text = Tx.Get("support"), ForeColor = Color.FromArgb(120, 30, 30) };
            ask.SetBounds(10, 8, 300, 48);
            Button kofi = new Button { Text = Tx.Get("donate"), BackColor = coral, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            kofi.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            kofi.FlatAppearance.BorderSize = 0;
            kofi.SetBounds(318, 12, 200, 40);
            kofi.Click += delegate { Open("https://ko-fi.com/xillo23"); };
            box.Controls.Add(ask);
            box.Controls.Add(kofi);
            Controls.AddRange(new Control[] { pick, path, browse, install, uninstall, status, box, notice, site });
            AcceptButton = install;
            path.Text = Detect() ?? "";
        }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = Tx.Get("pick");
                if (Directory.Exists(path.Text)) d.SelectedPath = path.Text;
                if (d.ShowDialog(this) == DialogResult.OK) path.Text = d.SelectedPath;
            }
        }

        void Run(Func<string, string> action)
        {
            string game = path.Text.Trim().Trim('"');
            status.ForeColor = Color.DarkRed;
            if (!File.Exists(Path.Combine(game, "Cuphead.exe"))) { status.Text = Tx.Get("notfound"); return; }
            if (Process.GetProcessesByName("Cuphead").Length > 0) { status.Text = Tx.Get("running"); return; }
            try
            {
                status.Text = action(game);
                status.ForeColor = Color.DarkGreen;
            }
            catch (Exception ex) { status.Text = Tx.Get("error") + ex.Message; }
        }

        // ---------- detection ----------

        static string Detect()
        {
            foreach (string dir in Candidates())
            {
                try { if (File.Exists(Path.Combine(dir, "Cuphead.exe"))) return dir; } catch { }
            }
            return null;
        }

        static IEnumerable<string> Candidates()
        {
            List<string> libs = new List<string>();
            string steam = Reg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                ?? Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            if (steam != null)
            {
                steam = steam.Replace('/', '\\');
                libs.Add(steam);
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }
            foreach (string lib in libs) yield return Path.Combine(lib, @"steamapps\common\Cuphead");
            foreach (string root in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
            {
                RegistryKey games = null;
                try { games = Registry.LocalMachine.OpenSubKey(root); } catch { }
                if (games == null) continue;
                foreach (string id in games.GetSubKeyNames())
                {
                    string dir = null;
                    try
                    {
                        using (RegistryKey k = games.OpenSubKey(id))
                            if (k != null && Convert.ToString(k.GetValue("gameName")).IndexOf("Cuphead", StringComparison.OrdinalIgnoreCase) >= 0)
                                dir = Convert.ToString(k.GetValue("path"));
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(dir)) yield return dir;
                }
            }
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Path.Combine(pf86, @"Steam\steamapps\common\Cuphead");
            yield return Path.Combine(pf86, @"GOG Galaxy\Games\Cuphead");
            yield return @"C:\GOG Games\Cuphead";
        }

        static string Reg(RegistryKey hive, string key, string name)
        {
            try { using (RegistryKey k = hive.OpenSubKey(key)) return k == null ? null : k.GetValue(name) as string; }
            catch { return null; }
        }

        // ---------- install / uninstall ----------

        static string Install(string game)
        {
            string manifest = Path.Combine(game, ManifestName);
            List<string> created = new List<string>();
            if (File.Exists(manifest)) created.AddRange(File.ReadAllLines(manifest));
            bool hadBepInEx = File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.dll")) && !created.Contains(@"BepInEx\core\BepInEx.dll");
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (e.Name.Length == 0) continue; // directory
                    string rel = e.FullName.Replace('/', '\\');
                    bool isLoader = !rel.StartsWith(@"BepInEx\plugins\", StringComparison.OrdinalIgnoreCase);
                    if (isLoader && hadBepInEx) continue; // keep the user's own BepInEx and its other mods
                    string dest = Path.Combine(game, rel);
                    bool existed = File.Exists(dest);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (Stream input = e.Open())
                    using (FileStream output = File.Create(dest)) input.CopyTo(output);
                    if (!existed && !created.Contains(rel)) created.Add(rel);
                }
            }
            if (!hadBepInEx && !created.Contains("#loader")) created.Add("#loader");
            File.WriteAllLines(manifest, created.ToArray());
            return hadBepInEx ? Tx.Get("done") + "\n" + Tx.Get("kept") : Tx.Get("done");
        }

        static string Uninstall(string game)
        {
            string manifest = Path.Combine(game, ManifestName);
            List<string> created = File.Exists(manifest) ? new List<string>(File.ReadAllLines(manifest)) : new List<string>();
            bool ownLoader = created.Contains("#loader");
            if (created.Count == 0) created.Add(@"BepInEx\plugins\CupheadLanCoop\CupheadLanCoop.dll"); // manual install: only the mod
            created.Add(@"BepInEx\config\it.cupheadlan.coop.cfg");
            created.Add(@"BepInEx\config\it.cupheadlan.coop.ultimo-ip.txt");
            if (ownLoader)
            {
                // Files BepInEx itself creates at runtime (only when we installed it).
                created.Add(@"BepInEx\config\BepInEx.cfg");
                created.Add(@"BepInEx\LogOutput.log");
                created.Add(@"BepInEx\LogOutput.log.1");
            }
            foreach (string rel in created)
            {
                if (rel.StartsWith("#")) continue;
                string f = Path.Combine(game, rel);
                if (File.Exists(f)) File.Delete(f);
            }
            DeleteTree(Path.Combine(game, @"BepInEx\coop-logs"));
            if (ownLoader) DeleteTree(Path.Combine(game, @"BepInEx\cache"));
            RemoveEmpty(Path.Combine(game, "BepInEx"));
            if (File.Exists(manifest)) File.Delete(manifest);
            return Tx.Get("removed");
        }

        static void DeleteTree(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        static void RemoveEmpty(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string d in Directory.GetDirectories(dir)) RemoveEmpty(d);
            if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
        }
    }
}
