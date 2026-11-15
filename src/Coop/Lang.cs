using System;
using System.Collections.Generic;
using System.Text;

namespace CupheadLanCoop
{
    // Mod texts in every language of the game, following the game language (Options > Language)
    // unless the config [General] Language forces one.
    //
    // Messages that travel between the two PCs (disconnect reasons, rejections) are sent encoded, so
    // each player reads them in their own language: segments separated by '\u001e', each segment is
    // either plain text or "#key" + '\u001f' + args. See Show().
    internal static class L
    {
        public const char Seg = '\u001e', Arg = '\u001f';
        public static string Forced = "auto";

        // Column order = Localization.Languages: English, French, Italian, German, SpanishSpain, SpanishAmerica,
        // Korean, Russian, Polish, PortugueseBrazil, Japanese, SimplifiedChinese.
        static readonly string[] Codes = { "en", "fr", "it", "de", "es", "es-419", "ko", "ru", "pl", "pt-br", "ja", "zh" };
        static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]>();

        static void Add(string key, params string[] v) { T[key] = v; }

        static L()
        {
            Add("menu", "ONLINE CO-OP", "COOP EN LIGNE", "CO-OP ONLINE", "ONLINE-KOOP", "COOPERATIVO ONLINE", "COOPERATIVO EN LÍNEA",
                "온라인 협동", "ОНЛАЙН-КООП", "KOOPERACJA ONLINE", "COOPERATIVO ONLINE", "オンライン協力プレイ", "在线合作");
            Add("host", "HOST GAME", "HÉBERGER UNE PARTIE", "CREA PARTITA", "SPIEL HOSTEN", "CREAR PARTIDA", "CREAR PARTIDA",
                "게임 만들기", "СОЗДАТЬ ИГРУ", "UTWÓRZ GRĘ", "CRIAR PARTIDA", "ゲームを作成", "创建游戏");
            Add("join", "JOIN GAME", "REJOINDRE", "UNISCITI", "SPIEL BEITRETEN", "UNIRSE", "UNIRSE",
                "참가하기", "ПРИСОЕДИНИТЬСЯ", "DOŁĄCZ", "ENTRAR", "参加する", "加入游戏");
            Add("back", "BACK", "RETOUR", "INDIETRO", "ZURÜCK", "VOLVER", "VOLVER",
                "뒤로", "НАЗАД", "WSTECZ", "VOLTAR", "戻る", "返回");
            Add("cancel", "CANCEL", "ANNULER", "ANNULLA", "ABBRECHEN", "CANCELAR", "CANCELAR",
                "취소", "ОТМЕНА", "ANULUJ", "CANCELAR", "キャンセル", "取消");
            Add("leave", "LEAVE", "QUITTER", "ESCI", "VERLASSEN", "SALIR", "SALIR",
                "나가기", "ВЫЙТИ", "WYJDŹ", "SAIR", "退出する", "退出");
            Add("player2", "PLAYER 2: {0}", "JOUEUR 2 : {0}", "GIOCATORE 2: {0}", "SPIELER 2: {0}", "JUGADOR 2: {0}", "JUGADOR 2: {0}",
                "플레이어 2: {0}", "ИГРОК 2: {0}", "GRACZ 2: {0}", "JOGADOR 2: {0}", "プレイヤー2: {0}", "玩家2：{0}");
            Add("choose_save", "CHOOSE SAVE FILE", "CHOISIR LA SAUVEGARDE", "SCEGLI SALVATAGGIO", "SPIELSTAND WÄHLEN", "ELEGIR PARTIDA GUARDADA", "ELEGIR PARTIDA GUARDADA",
                "저장 파일 선택", "ВЫБРАТЬ СОХРАНЕНИЕ", "WYBIERZ ZAPIS", "ESCOLHER JOGO SALVO", "セーブデータを選択", "选择存档");
            Add("waiting", "WAITING FOR PLAYER 2...", "EN ATTENTE DU JOUEUR 2...", "IN ATTESA DEL GIOCATORE 2...", "WARTE AUF SPIELER 2...", "ESPERANDO AL JUGADOR 2...", "ESPERANDO AL JUGADOR 2...",
                "플레이어 2를 기다리는 중...", "ОЖИДАНИЕ ИГРОКА 2...", "CZEKANIE NA GRACZA 2...", "AGUARDANDO O JOGADOR 2...", "プレイヤー2を待っています...", "等待玩家2...");
            Add("back_cancels", "(BACK = CANCEL)", "(RETOUR = ANNULER)", "(INDIETRO = ANNULLA)", "(ZURÜCK = ABBRECHEN)", "(VOLVER = CANCELAR)", "(VOLVER = CANCELAR)",
                "(뒤로 = 취소)", "(НАЗАД = ОТМЕНА)", "(WSTECZ = ANULUJ)", "(VOLTAR = CANCELAR)", "(戻る = キャンセル)", "(返回 = 取消)");
            Add("searching", "SEARCHING FOR GAMES...", "RECHERCHE DE PARTIES...", "RICERCA PARTITE...", "SUCHE NACH SPIELEN...", "BUSCANDO PARTIDAS...", "BUSCANDO PARTIDAS...",
                "게임 검색 중...", "ПОИСК ИГР...", "SZUKANIE GIER...", "PROCURANDO PARTIDAS...", "ゲームを検索中...", "正在搜索游戏...");
            Add("version", "(VERSION {0})", "(VERSION {0})", "(VERSIONE {0})", "(VERSION {0})", "(VERSIÓN {0})", "(VERSIÓN {0})",
                "(버전 {0})", "(ВЕРСИЯ {0})", "(WERSJA {0})", "(VERSÃO {0})", "(バージョン {0})", "(版本 {0})");
            Add("enter_ip", "ENTER IP", "SAISIR UNE IP", "INSERISCI IP", "IP EINGEBEN", "INTRODUCIR IP", "INGRESAR IP",
                "IP 입력", "ВВЕСТИ IP", "WPISZ IP", "DIGITAR IP", "IPを入力", "输入IP");
            Add("connect", "CONNECT", "SE CONNECTER", "CONNETTI", "VERBINDEN", "CONECTAR", "CONECTAR",
                "연결", "ПОДКЛЮЧИТЬСЯ", "POŁĄCZ", "CONECTAR", "接続", "连接");
            Add("connecting", "CONNECTING...", "CONNEXION...", "CONNESSIONE...", "VERBINDE...", "CONECTANDO...", "CONECTANDO...",
                "연결 중...", "ПОДКЛЮЧЕНИЕ...", "ŁĄCZENIE...", "CONECTANDO...", "接続中...", "正在连接...");
            Add("connected_to", "CONNECTED TO {0}", "CONNECTÉ À {0}", "CONNESSO A {0}", "VERBUNDEN MIT {0}", "CONECTADO A {0}", "CONECTADO A {0}",
                "{0}에 연결됨", "ПОДКЛЮЧЕНО К {0}", "POŁĄCZONO Z {0}", "CONECTADO A {0}", "{0}に接続しました", "已连接到{0}");
            Add("host_choosing", "THE HOST IS CHOOSING THE SAVE FILE...", "L'HÔTE CHOISIT LA SAUVEGARDE...", "L'HOST STA SCEGLIENDO IL SALVATAGGIO...",
                "DER HOST WÄHLT DEN SPIELSTAND...", "EL ANFITRIÓN ESTÁ ELIGIENDO LA PARTIDA...", "EL ANFITRIÓN ESTÁ ELIGIENDO LA PARTIDA...",
                "호스트가 저장 파일을 선택하는 중...", "ХОСТ ВЫБИРАЕТ СОХРАНЕНИЕ...", "HOST WYBIERA ZAPIS...", "O ANFITRIÃO ESTÁ ESCOLHENDO O JOGO SALVO...",
                "ホストがセーブデータを選択中...", "主机正在选择存档...");
            Add("slow_net", "Slow connection: waiting for your partner...", "Connexion lente : en attente du partenaire...", "Rete lenta: attendo il compagno...",
                "Langsame Verbindung: warte auf den Partner...", "Conexión lenta: esperando al compañero...", "Conexión lenta: esperando al compañero...",
                "연결 지연: 파트너를 기다리는 중...", "Медленное соединение: ждём напарника...", "Wolne połączenie: czekam na partnera...",
                "Conexão lenta: aguardando o parceiro...", "通信が遅れています：相手を待っています...", "网络缓慢：正在等待队友...");
            Add("partner_quit", "Your partner closed the game.", "Votre partenaire a fermé le jeu.", "Il compagno ha chiuso il gioco.", "Dein Partner hat das Spiel beendet.",
                "Tu compañero ha cerrado el juego.", "Tu compañero cerró el juego.", "파트너가 게임을 종료했습니다.", "Напарник закрыл игру.", "Partner zamknął grę.",
                "Seu parceiro fechou o jogo.", "相手がゲームを終了しました。", "队友已关闭游戏。");
            Add("partner_cancel", "Your partner cancelled.", "Votre partenaire a annulé.", "Il compagno ha annullato.", "Dein Partner hat abgebrochen.",
                "Tu compañero ha cancelado.", "Tu compañero canceló.", "파트너가 취소했습니다.", "Напарник отменил подключение.", "Partner anulował.",
                "Seu parceiro cancelou.", "相手がキャンセルしました。", "队友已取消。");
            Add("no_response", "Your partner has not responded for 8 seconds.", "Votre partenaire ne répond plus depuis 8 secondes.", "Il compagno non risponde da 8 secondi.",
                "Dein Partner antwortet seit 8 Sekunden nicht.", "Tu compañero no responde desde hace 8 segundos.", "Tu compañero no responde hace 8 segundos.",
                "파트너가 8초 동안 응답하지 않습니다.", "Напарник не отвечает 8 секунд.", "Partner nie odpowiada od 8 sekund.", "Seu parceiro não responde há 8 segundos.",
                "相手から8秒間応答がありません。", "队友已8秒无响应。");
            Add("conn_lost", "Connection lost:", "Connexion perdue :", "Connessione persa:", "Verbindung verloren:", "Conexión perdida:", "Conexión perdida:",
                "연결 끊김:", "Соединение потеряно:", "Utracono połączenie:", "Conexão perdida:", "接続が切れました：", "连接中断：");
            Add("peer_closed", "the partner closed the connection.", "le partenaire a fermé la connexion.", "il compagno ha chiuso la connessione.",
                "der Partner hat die Verbindung geschlossen.", "el compañero cerró la conexión.", "el compañero cerró la conexión.",
                "파트너가 연결을 닫았습니다.", "напарник закрыл соединение.", "partner zamknął połączenie.", "o parceiro fechou a conexão.",
                "相手が接続を閉じました。", "队友关闭了连接。");
            Add("conn_broken", "connection interrupted.", "connexion interrompue.", "connessione interrotta.", "Verbindung unterbrochen.", "conexión interrumpida.", "conexión interrumpida.",
                "연결이 중단되었습니다.", "соединение прервано.", "połączenie przerwane.", "conexão interrompida.", "接続が中断されました。", "连接已中断。");
            Add("internal", "Internal error:", "Erreur interne :", "Errore interno:", "Interner Fehler:", "Error interno:", "Error interno:",
                "내부 오류:", "Внутренняя ошибка:", "Błąd wewnętrzny:", "Erro interno:", "内部エラー：", "内部错误：");
            Add("timeout", "Timed out waiting for your partner.", "Délai dépassé en attendant le partenaire.", "Tempo scaduto in attesa del compagno.",
                "Zeitüberschreitung beim Warten auf den Partner.", "Tiempo de espera agotado esperando al compañero.", "Se agotó el tiempo de espera del compañero.",
                "파트너 대기 시간이 초과되었습니다.", "Время ожидания напарника истекло.", "Przekroczono czas oczekiwania na partnera.", "Tempo esgotado aguardando o parceiro.",
                "相手の待機がタイムアウトしました。", "等待队友超时。");
            Add("no_answer", "No answer from {0}.", "Aucune réponse de {0}.", "Nessuna risposta da {0}.", "Keine Antwort von {0}.", "Sin respuesta de {0}.", "Sin respuesta de {0}.",
                "{0}에서 응답이 없습니다.", "Нет ответа от {0}.", "Brak odpowiedzi od {0}.", "Sem resposta de {0}.", "{0}から応答がありません。", "{0}无响应。");
            Add("connect_failed", "Could not connect to {0}:", "Impossible de se connecter à {0} :", "Connessione a {0} fallita:", "Verbindung zu {0} fehlgeschlagen:",
                "No se pudo conectar a {0}:", "No se pudo conectar a {0}:", "{0}에 연결할 수 없습니다:", "Не удалось подключиться к {0}:", "Nie można połączyć z {0}:",
                "Não foi possível conectar a {0}:", "{0}に接続できません：", "无法连接到{0}：");
            Add("port_busy", "TCP port {0} is not available:", "Le port TCP {0} n'est pas disponible :", "Porta TCP {0} non disponibile:", "TCP-Port {0} nicht verfügbar:",
                "El puerto TCP {0} no está disponible:", "El puerto TCP {0} no está disponible:", "TCP 포트 {0}을(를) 사용할 수 없습니다:", "TCP-порт {0} недоступен:",
                "Port TCP {0} jest niedostępny:", "A porta TCP {0} não está disponível:", "TCPポート{0}は使用できません：", "TCP端口{0}不可用：");
            Add("rejected", "{0} was rejected:", "{0} a été refusé :", "{0} rifiutato:", "{0} wurde abgelehnt:", "{0} fue rechazado:", "{0} fue rechazado:",
                "{0} 거부됨:", "{0} отклонён:", "{0} odrzucony:", "{0} foi recusado:", "{0}を拒否しました：", "已拒绝{0}：");
            Add("version_diff", "Different mod version: host {0}, guest {1}.", "Version du mod différente : hôte {0}, invité {1}.", "Versione mod diversa: host {0}, ospite {1}.",
                "Unterschiedliche Mod-Version: Host {0}, Gast {1}.", "Versión del mod distinta: anfitrión {0}, invitado {1}.", "Versión del mod distinta: anfitrión {0}, invitado {1}.",
                "모드 버전이 다릅니다: 호스트 {0}, 게스트 {1}.", "Разные версии мода: хост {0}, гость {1}.", "Inna wersja moda: host {0}, gość {1}.",
                "Versão do mod diferente: anfitrião {0}, convidado {1}.", "MODのバージョンが違います：ホスト{0}、ゲスト{1}。", "模组版本不同：主机{0}，客机{1}。");
            Add("install_diff", "Different installations (same game version, DLC and mods needed): {0}", "Installations différentes (même version du jeu, DLC et mods requis) : {0}",
                "Installazioni diverse (servono stessa versione, DLC e mod): {0}", "Unterschiedliche Installationen (gleiche Spielversion, DLC und Mods nötig): {0}",
                "Instalaciones distintas (se necesita la misma versión, DLC y mods): {0}", "Instalaciones distintas (se necesita la misma versión, DLC y mods): {0}",
                "설치가 다릅니다 (같은 게임 버전, DLC, 모드 필요): {0}", "Разные установки (нужны одинаковые версия игры, DLC и моды): {0}",
                "Różne instalacje (wymagana ta sama wersja gry, DLC i mody): {0}", "Instalações diferentes (mesma versão do jogo, DLC e mods necessários): {0}",
                "インストール内容が違います（同じゲームバージョン・DLC・MODが必要）：{0}", "安装不一致（需要相同的游戏版本、DLC和模组）：{0}");
        }

        static int Index()
        {
            if (Forced != null && Forced.Length > 0 && Forced != "auto")
            {
                int f = Array.IndexOf(Codes, Forced.ToLowerInvariant());
                if (f >= 0) return f;
            }
            int i;
            try { i = (int)Localization.language; } catch { i = 0; }
            return i >= 0 && i < Codes.Length ? i : 0;
        }

        // Translated text for a key (args fill {0}, {1}...).
        public static string Tr(string key, params object[] args)
        {
            string[] v;
            if (!T.TryGetValue(key, out v)) return key;
            int i = Index();
            string s = i < v.Length && v[i] != null ? v[i] : v[0];
            try { return args == null || args.Length == 0 ? s : string.Format(s, args); }
            catch (FormatException) { return s; }
        }

        // Encoded message, to be shown later (possibly on the other PC) with Show().
        public static string M(string key, params object[] args)
        {
            StringBuilder b = new StringBuilder("#").Append(key);
            if (args != null) foreach (object a in args) b.Append(Arg).Append(a == null ? "" : a.ToString().Replace(Seg, ' ').Replace(Arg, ' '));
            return b.ToString();
        }

        public static string Join(params string[] segments) { return string.Join(Seg.ToString(), segments); }

        public static string Show(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return encoded;
            string[] segs = encoded.Split(Seg);
            for (int s = 0; s < segs.Length; s++)
            {
                string g = segs[s];
                if (g.Length < 2 || g[0] != '#') continue;
                string[] parts = g.Substring(1).Split(Arg);
                object[] args = new object[parts.Length - 1];
                Array.Copy(parts, 1, args, 0, args.Length);
                segs[s] = T.ContainsKey(parts[0]) ? Tr(parts[0], args) : g;
            }
            return string.Join(" ", segs);
        }
    }
}
