using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SummerCoop.Launcher
{
    internal sealed partial class MainForm
    {
        private Panel pageLobby, pageGuide, pageNotes;
        private MwGlassPanel rail;
        private Label pageTitle, pageSubtitle, gameFooter;
        private MwButton netSteam, netLan, hostTile, joinTile, navLobby, navGuide, navNotes, themeButton, btnLoader;
        private readonly Dictionary<Control, RectangleF> viewBounds = new Dictionary<Control, RectangleF>();
        private readonly Dictionary<Control, float> viewFonts = new Dictionary<Control, float>();
        private bool layingOut;
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);

        private void Place(Control parent, Control child, float x, float y, float w, float h)
        {
            parent.Controls.Add(child); child.Margin=Padding.Empty;
            viewBounds[child]=new RectangleF(x,y,w,h);
            viewFonts[child]=child.Font.SizeInPoints*96/72;
            child.Bounds=Rectangle.Round(viewBounds[child]);
        }
        private Label Caption(Control parent, string text, float x, float y, float w, float h, float size, bool bold)
        {
            Label l=new Label {Text=text,BackColor=Color.Transparent,ForeColor=MwStyle.Ink,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel)};
            Place(parent,l,x,y,w,h); return l;
        }
        private MwButton ViewButton(Control parent,string text,string icon,float x,float y,float w,float h,Action click)
        {
            MwButton b=(MwButton)MakeButton(text,false);b.IconName=icon;b.AutoSize=false;b.MinimumSize=Size.Empty;b.AccessibleName=text.Length>0?text:icon;
            Place(parent,b,x,y,w,h);b.Click+=delegate{click();};return b;
        }
        private void BuildNavigation()
        {
            rail=new MwGlassPanel();Place(content,rail,16,16,232,768);
            Caption(rail,"SUMMER",22,22,188,28,27,true).TextAlign=ContentAlignment.MiddleCenter;
            Caption(rail,"COOP",22,48,188,46,32,true).TextAlign=ContentAlignment.MiddleCenter;
            Label version=Caption(rail,"v"+LauncherInfo.ModVersion,34,100,164,22,11,false);version.ForeColor=MwStyle.Grey;version.TextAlign=ContentAlignment.MiddleCenter;
            Caption(rail,"ИГРАТЬ",24,132,180,24,11,true).ForeColor=MwStyle.Grey;
            navHome=ViewButton(rail,"Главная","home",12,160,208,44,delegate{ShowPage(pageHome);RefreshStatuses();});
            navLobby=ViewButton(rail,"Подключение","users",12,208,208,44,delegate{ShowPage(pageLobby);});
            Caption(rail,"ИНФОРМАЦИЯ",24,300,180,24,11,true).ForeColor=MwStyle.Grey;
            navGuide=ViewButton(rail,"Как играть","book",12,328,208,44,delegate{ShowPage(pageGuide);});
            navNotes=ViewButton(rail,"Что нового","file",12,376,208,44,delegate{ShowPage(pageNotes);});
            navLogs=ViewButton(rail,"Журналы","file",12,424,208,44,delegate{ShowPage(pageLogs);RefreshLogs();});
            navSettings=ViewButton(rail,"Настройки","gear",12,658,208,44,delegate{ShowPage(pageSettings);});
            foreach(Button b in new Button[]{navHome,navLobby,navGuide,navNotes,navLogs,navSettings}) ((MwButton)b).Navigation=true;
            pageTitle=Caption(content,"",268,16,700,34,26,true);
            pageSubtitle=Caption(content,"",270,50,700,22,13,false);pageSubtitle.ForeColor=MwStyle.Grey;
            pageTitle.MouseDown+=DragWindow;pageSubtitle.MouseDown+=DragWindow;
            content.MouseDown+=DragWindow;
            themeButton=ViewButton(content,"","sun",1060,22,44,44,ToggleTheme);
            ViewButton(content,"","min",1114,22,44,44,delegate{WindowState=FormWindowState.Minimized;});
            ViewButton(content,"","max",1166,22,44,44,delegate{WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;});
            ViewButton(content,"","close",1218,22,44,44,Close);
        }
        private void DragWindow(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Left)return;
            if(e.Clicks==2){WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;return;}
            ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);
        }
        private static void OpenUrl(string url) { try { ProcessStart(url); } catch(Exception){} }
        private static void ProcessStart(string url) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true}); }
        private void BuildStatus()
        {
            lblStatus=Caption(content,"Выберите способ подключения и создайте игру.",268,752,650,32,12,true);
            gameFooter=Caption(content,"My Summer Car",938,752,324,32,12,false);gameFooter.TextAlign=ContentAlignment.MiddleRight;
        }
        private Panel MakePage()
        {
            Panel p=new Panel {BackColor=Color.Transparent,Visible=false};
            Place(content,p,264,88,1000,656);return p;
        }
        private TableLayoutPanel Card(Panel page,string title)
        {
            MwGlassPanel glass=new MwGlassPanel{Dock=DockStyle.Fill,Padding=new Padding(24)};page.Controls.Add(glass);
            Panel scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.Transparent};glass.Controls.Add(scroll);
            TableLayoutPanel table=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,BackColor=Color.Transparent,Padding=new Padding(0,0,6,8)};
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));scroll.Controls.Add(table);
            Label heading=MakeLabel(title);heading.Font=new Font("Segoe UI",22,FontStyle.Bold,GraphicsUnit.Pixel);heading.Margin=new Padding(0,0,0,20);Add(table,heading);return table;
        }
        private static void Add(TableLayoutPanel table,Control control)
        {
            int row=table.RowCount++;table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.Controls.Add(control,0,row);
        }
        private static Label MakeLabel(string text)
        {
            return new Label{Text=text,AutoSize=true,Dock=DockStyle.Fill,Margin=new Padding(0,0,0,10),BackColor=Color.Transparent,ForeColor=MwStyle.Ink};
        }
        private static Button MakeButton(string text,bool primary)
        {
            return new MwButton{Text=text,Primary=primary,AutoSize=true,MinimumSize=new Size(100,38),Padding=new Padding(12,4,12,4),Font=new Font("Segoe UI",13.333f,FontStyle.Regular,GraphicsUnit.Pixel),Margin=new Padding(0,4,10,0)};
        }
        private static Control Field(TableLayoutPanel card,string caption,Control input)
        {
            TableLayoutPanel row=new TableLayoutPanel{Height=48,Dock=DockStyle.Fill,Margin=new Padding(0,0,0,10),ColumnCount=2,RowCount=1,BackColor=Color.Transparent};
            row.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,170));row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Label label=MakeLabel(caption);label.Anchor=AnchorStyles.Left;label.Margin=new Padding(0,6,12,4);label.ForeColor=MwStyle.Grey;row.Controls.Add(label,0,0);
            if(input is TextBox || input is ComboBox) {
                input.BackColor=MwStyle.Field;input.ForeColor=MwStyle.Ink;
                TextBox text=input as TextBox;if(text!=null)text.BorderStyle=BorderStyle.FixedSingle;
            }
            input.Dock=DockStyle.Fill;input.Margin=new Padding(0,5,0,5);row.Controls.Add(input,1,0);Add(card,row);return row;
        }
        private static ComboBox Choice(params string[] items)
        {
            ComboBox c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,BackColor=MwStyle.Field,ForeColor=MwStyle.Ink,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=24};
            c.DrawItem+=delegate(object sender,DrawItemEventArgs e){
                if(e.Index<0)return;
                using(SolidBrush b=new SolidBrush(MwStyle.Field))e.Graphics.FillRectangle(b,e.Bounds);
                TextRenderer.DrawText(e.Graphics,Convert.ToString(c.Items[e.Index]),c.Font,e.Bounds,MwStyle.Ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };c.Items.AddRange(items);return c;
        }
        private static CheckBox Option(string text) { return new CheckBox{Text=text,AutoSize=true,Margin=new Padding(0,0,0,14),ForeColor=MwStyle.Ink,BackColor=Color.Transparent}; }
        private void BuildHome()
        {
            pageLobby=MakePage();pageGuide=MakePage();pageNotes=MakePage();
            MwGlassPanel profile=new MwGlassPanel();Place(pageHome,profile,0,0,340,656);
            Caption(profile,"ТВОЙ ПЕРСОНАЖ",20,16,300,24,11,true).ForeColor=MwStyle.Grey;
            Panel stage=new Panel{BackColor=Color.Transparent};Place(profile,stage,18,48,304,426);
            stage.Paint+=delegate(object sender,PaintEventArgs e) {
                Graphics g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float sx=stage.Width/304f,sy=stage.Height/426f;g.ScaleTransform(sx,sy);
                MwStyle.Fill(g,new RectangleF(.5f,.5f,303,425),18,Color.FromArgb(20,MwStyle.Ink),Color.FromArgb(30,MwStyle.Ink));
                using(SolidBrush b=new SolidBrush(Color.FromArgb(50,MwStyle.Grey))) {
                    g.FillEllipse(b,122,80,60,64);using(System.Drawing.Drawing2D.GraphicsPath p=MwStyle.Round(new RectangleF(96,156,112,150),30))g.FillPath(b,p);
                }
                MwStyle.Text(g,"Внешний вид персонажа\nможно изменить в игре: F10",new RectangleF(24,330,256,56),13,MwStyle.Grey,false,StringAlignment.Center);
            };
            Caption(profile,"ИМЯ ИГРОКА",20,486,300,20,11,true).ForeColor=MwStyle.Grey;
            txtName=new TextBox{MaxLength=48,BorderStyle=BorderStyle.FixedSingle,BackColor=MwStyle.Field,ForeColor=MwStyle.Ink,Font=new Font("Segoe UI",18,FontStyle.Bold,GraphicsUnit.Pixel)};
            Place(profile,txtName,18,518,304,32);
            ViewButton(profile,"Как играть","book",18,572,148,44,delegate{ShowPage(pageGuide);});
            ViewButton(profile,"Настройки","gear",174,572,148,44,delegate{ShowPage(pageSettings);});
            MwGlassPanel network=new MwGlassPanel();Place(pageHome,network,360,0,640,188);
            Caption(network,"Способ подключения",20,16,300,24,17,true);
            Caption(network,"Выберите одинаковый у всех",308,16,310,24,12,false).ForeColor=MwStyle.Grey;
            netSteam=ViewButton(network,"STEAM","steam",20,50,295,64,delegate{SelectNetwork(true);});
            netLan=ViewButton(network,"IP / VPN","wifi",325,50,295,64,delegate{SelectNetwork(false);});
            lblSteam=Caption(network,"Steam: ожидание подключения",20,126,450,40,12,false);lblSteam.ForeColor=MwStyle.Grey;
            ViewButton(network,"Настроить","gear",486,126,134,40,delegate{ShowPage(pageSettings);});
            hostTile=ViewButton(pageHome,"СОЗДАТЬ","host",360,204,312,196,delegate{SelectRole(true);});hostTile.Primary=true;hostTile.Tile=true;
            hostTile.Description="Открой свою игру и пригласи друзей через Steam или по IP.";
            joinTile=ViewButton(pageHome,"ПРИСОЕДИНИТЬСЯ","join",688,204,312,196,delegate{SelectRole(false);});joinTile.Tile=true;
            joinTile.Description="Прими приглашение друга или укажи адрес его игры.";
            ViewButton(pageHome,"Одиночная игра","play",360,416,640,58,delegate{cmbMode.SelectedIndex=(int)ConnectionMode.Solo;ShowPage(pageLobby);});
            MwGlassPanel news=new MwGlassPanel();Place(pageHome,news,360,490,640,166);
            Caption(news,"ЧТО НОВОГО",20,12,440,24,11,true).ForeColor=MwStyle.Grey;
            Caption(news,"SummerCoop "+LauncherInfo.ModVersion,20,42,580,26,18,true);
            Caption(news,"Возрождение, жидкости автомобиля и предметы острова.\nПереноска дров, покупки и совместный сон.",20,80,580,54,14,false).ForeColor=MwStyle.Grey;
            MwGlassPanel invitation=new MwGlassPanel();Place(pageHome,invitation,360,490,640,166);
            Caption(invitation,"ПРИГЛАШЕНИЕ STEAM",20,12,580,24,11,true).ForeColor=MwStyle.Accent;
            cmbInvites=Choice();Place(invitation,cmbInvites,20,44,600,30);
            MwButton accept=ViewButton(invitation,"Принять и играть","join",20,98,292,46,AcceptInvitation);accept.Primary=true;
            ViewButton(invitation,"Отклонить","",328,98,292,46,DismissInvitation);
            inviteRow=invitation;inviteRow.Visible=false;
            BuildLobby();BuildGuide();BuildNotes();
        }
        private void BuildLobby()
        {
            TableLayoutPanel card=Card(pageLobby,"Параметры игры");
            cmbMode=Choice("Одиночная игра","Создать по IP / VPN","Подключиться по IP / VPN","Создать через Steam","Подключиться через Steam");
            Field(card,"Режим",cmbMode);cmbMode.SelectedIndexChanged+=delegate{if(!loading)RefreshModeHint();};
            lblModeDesc=MakeLabel("");lblModeDesc.ForeColor=MwStyle.Grey;Add(card,lblModeDesc);
            txtAddress=new TextBox();addressRow=Field(card,"Адрес хоста",txtAddress);
            txtPort=new TextBox{MaxLength=5};portRow=Field(card,"Порт",txtPort);
            txtPassword=new TextBox{UseSystemPasswordChar=true};passwordRow=Field(card,"Пароль (если есть)",txtPassword);
            txtLobby=new TextBox();lobbyRow=Field(card,"Номер лобби Steam",txtLobby);
            lblGame=MakeLabel("");lblGame.Margin=new Padding(0,14,0,8);Add(card,lblGame);
            lblMod=MakeLabel("");lblMod.ForeColor=MwStyle.Grey;Add(card,lblMod);
            FlowLayoutPanel actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,BackColor=Color.Transparent,Margin=Padding.Empty};
            btnPlay=MakeButton("Играть",true);btnPlay.MinimumSize=new Size(220,48);((MwButton)btnPlay).IconName="play";btnPlay.Click+=delegate{Play();};
            btnInstall=MakeButton("Установить мод",false);btnInstall.MinimumSize=new Size(180,48);((MwButton)btnInstall).IconName="download";btnInstall.Click+=delegate{InstallMod();};
            btnLoader=(MwButton)MakeButton("Скачать MSCLoader",false);btnLoader.MinimumSize=new Size(200,48);btnLoader.IconName="download";btnLoader.Click+=delegate{OpenUrl(LoaderDownloadUrl);};btnLoader.Visible=false;
            actions.Controls.Add(btnPlay);actions.Controls.Add(btnInstall);actions.Controls.Add(btnLoader);Add(card,actions);
        }
        private void SelectNetwork(bool steam)
        {
            ConnectionMode mode=(ConnectionMode)Math.Max(0,cmbMode.SelectedIndex);
            bool guest=mode==ConnectionMode.JoinLan || mode==ConnectionMode.JoinSteam;
            cmbMode.SelectedIndex=(int)(steam?(guest?ConnectionMode.JoinSteam:ConnectionMode.HostSteam):(guest?ConnectionMode.JoinLan:ConnectionMode.HostLan));
        }
        private void SelectRole(bool host)
        {
            bool steam=cmbMode.SelectedIndex==(int)ConnectionMode.HostSteam || cmbMode.SelectedIndex==(int)ConnectionMode.JoinSteam;
            cmbMode.SelectedIndex=(int)(steam?(host?ConnectionMode.HostSteam:ConnectionMode.JoinSteam):(host?ConnectionMode.HostLan:ConnectionMode.JoinLan));
            ShowPage(pageLobby);
        }
        private void BuildSettings()
        {
            TableLayoutPanel card=Card(pageSettings,"Игра и сохранения");
            cmbGame=new ComboBox{BackColor=MwStyle.Field,ForeColor=MwStyle.Ink};Field(card,"Папка игры",PathPicker(cmbGame,"Выберите папку с mysummercar.exe"));
            cmbGame.TextChanged+=delegate{if(!loading)RefreshStatuses();};
            txtProfile=new TextBox{BackColor=MwStyle.Field,ForeColor=MwStyle.Ink};Field(card,"Сохранение мира",PathPicker(txtProfile,"Выберите папку сохранения мира"));
            Label hint=MakeLabel("Мир хранится отдельно от одиночного сохранения.");hint.ForeColor=MwStyle.Grey;Add(card,hint);
            chkImport=Option("Начать новый мир с копии одиночного сохранения");Add(card,chkImport);
            chkDisable=Option("Отключить другие моды при установке");Add(card,chkDisable);
            chkClose=Option("Закрывать лаунчер после запуска игры");Add(card,chkClose);
            cmbMethod=Choice("Автоматически","Напрямую","Через Steam");Field(card,"Способ запуска",cmbMethod);
            Button save=MakeButton("Сохранить",true);save.Click+=delegate{if(SaveUIToSettings(true)){RefreshStatuses();AppendHome("Настройки сохранены.");}};Add(card,save);
            Label steamHint=MakeLabel("Приглашения Steam: добавьте строку ниже в Steam → My Summer Car → Свойства → Параметры запуска.");steamHint.Margin=new Padding(0,22,0,8);steamHint.ForeColor=MwStyle.Grey;Add(card,steamHint);
            TextBox steamOption=new TextBox{ReadOnly=true,Dock=DockStyle.Fill,BackColor=MwStyle.Field,ForeColor=MwStyle.Ink,Text=GameArgs.Quote(Assembly.GetExecutingAssembly().Location)+" %command%"};Add(card,steamOption);
            Button copy=MakeButton("Скопировать строку для Steam",false);copy.Click+=delegate{try{Clipboard.SetText(steamOption.Text);AppendHome("Вставьте строку в параметры запуска My Summer Car в Steam.");}catch(Exception e){AppendHome(e.Message);}};Add(card,copy);
        }
        private Control PathPicker(Control input,string description)
        {
            TableLayoutPanel picker=new TableLayoutPanel{Height=42,ColumnCount=2,RowCount=1,BackColor=Color.Transparent};
            picker.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));
            input.Dock=DockStyle.Fill;input.Anchor=AnchorStyles.Left|AnchorStyles.Right;input.Margin=new Padding(0,5,10,4);picker.Controls.Add(input,0,0);
            Button browse=MakeButton("Обзор…",false);browse.Margin=Padding.Empty;browse.Dock=DockStyle.Fill;
            browse.Click+=delegate{using(FolderBrowserDialog dialog=new FolderBrowserDialog()){dialog.Description=description;if(Directory.Exists(input.Text))dialog.SelectedPath=input.Text;if(dialog.ShowDialog(this)==DialogResult.OK)input.Text=dialog.SelectedPath;}};
            picker.Controls.Add(browse,1,0);return picker;
        }
        private void BuildLogs()
        {
            TableLayoutPanel card=Card(pageLogs,"Журнал текущего профиля");
            txtLogs=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Height=420,Font=new Font("Consolas",12,GraphicsUnit.Pixel),BackColor=MwStyle.Field,ForeColor=MwStyle.Ink,BorderStyle=BorderStyle.FixedSingle};Add(card,txtLogs);
            FlowLayoutPanel actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,BackColor=Color.Transparent};
            Button refresh=MakeButton("Обновить",false);refresh.Click+=delegate{RefreshLogs();};actions.Controls.Add(refresh);
            Button profile=MakeButton("Папка сохранения",false);profile.Click+=delegate{OpenFolder(txtProfile.Text);};actions.Controls.Add(profile);
            Button game=MakeButton("Папка игры",false);game.Click+=delegate{OpenFolder(cmbGame.Text);};actions.Controls.Add(game);Add(card,actions);
        }
        private void BuildGuide()
        {
            TableLayoutPanel card=Card(pageGuide,"Игра с друзьями");
            foreach(string text in new[]{"1. У всех игроков должна быть одна версия SummerCoop. Укажите папку My Summer Car в настройках и установите мод.","2. На главной выберите Steam или IP / VPN. Хост нажимает «Создать», проверяет параметры и запускает игру.","3. Steam: хост открывает F10 в игре и приглашает друга. У друга приглашение появится в лаунчере — нажмите «Принять и играть».","4. IP / VPN: гость выбирает «Присоединиться» и указывает IP и порт хоста. При необходимости введите пароль.","Сохранение хоста передаётся гостям автоматически. Совместный мир находится в отдельном профиле.","В игре: F10 — меню MWCoop, F6 — подключение. Внешний вид персонажа и игровые параметры доступны в меню F10."}) {
                Label l=MakeLabel(text);l.MaximumSize=new Size(870,0);l.Margin=new Padding(0,0,0,24);Add(card,l);
            }
        }
        private void BuildNotes()
        {
            TableLayoutPanel card=Card(pageNotes,"SummerCoop "+LauncherInfo.ModVersion);
            foreach(string text in new[]{"Гараж: синхронизация работы домкратов и положения поднятой машины.","Лаунчер: оформление и компоновка MWCoop, боковое меню, светлая и тёмная темы, отдельная страница подключения.","Steam: входящие приглашения, принятие и отклонение, выбор точного лобби.","Основа проекта — MWCoop 0.95.3, адаптированная для My Summer Car."}) {
                Label l=MakeLabel(text);l.MaximumSize=new Size(870,0);l.Margin=new Padding(0,0,0,24);Add(card,l);
            }
        }
        private void ShowPage(Panel page)
        {
            foreach(Panel p in new[]{pageHome,pageLobby,pageSettings,pageLogs,pageGuide,pageNotes})if(p!=null)p.Visible=p==page;
            AcceptButton=page==pageLobby?btnPlay:null;
            Highlight(navHome,page==pageHome);Highlight(navLobby,page==pageLobby);Highlight(navSettings,page==pageSettings);Highlight(navLogs,page==pageLogs);Highlight(navGuide,page==pageGuide);Highlight(navNotes,page==pageNotes);
            pageTitle.Text=page==pageHome?"Привет, "+(txtName==null?"игрок":txtName.Text):page==pageLobby?"Совместная игра":page==pageSettings?"Настройки":page==pageLogs?"Журналы":page==pageGuide?"Как играть":"Что нового";
            pageSubtitle.Text=page==pageHome?"My Summer Car вместе с друзьями":page==pageLobby?"Создай игру или присоединись к друзьям":page==pageSettings?"Игра, профиль мира и приглашения Steam":page==pageLogs?"Данные для диагностики проблем":page==pageGuide?"От установки до первого приглашения":"Изменения в текущей версии";
            LayoutView();
        }
        private static void Highlight(Button b,bool active) {if(b==null)return;((MwButton)b).Selected=active;b.Invalidate();}
        private void ToggleTheme()
        {
            MwStyle.Dark=!MwStyle.Dark;settings.DarkTheme=MwStyle.Dark;
            Recolor(content);themeButton.IconName=MwStyle.Dark?"sun":"moon";((MwScenePanel)content).ResetScene();SaveUIToSettings(true);RefreshStatuses();
        }
        private void Recolor(Control parent)
        {
            foreach(Control c in parent.Controls){
                Color old=c.ForeColor;
                c.ForeColor=old==Color.FromArgb(164,178,202)||old==Color.FromArgb(74,90,112)?MwStyle.Grey:old==Color.FromArgb(166,225,255)||old==Color.FromArgb(26,98,172)?MwStyle.Accent:MwStyle.Ink;
                if(c is TextBox || c is ComboBox)c.BackColor=MwStyle.Field;Recolor(c);
            }
            pageSubtitle.ForeColor=MwStyle.Grey;lblSteam.ForeColor=MwStyle.Grey;
        }
        private void LayoutView()
        {
            if(layingOut || pageTitle==null)return;layingOut=true;
            try {
                float sx=ClientSize.Width/1280f,sy=ClientSize.Height/800f,k=Math.Min(sx,sy);
                content.SuspendLayout();
                foreach(KeyValuePair<Control,RectangleF> kv in viewBounds){RectangleF r=kv.Value;kv.Key.Bounds=new Rectangle((int)Math.Round(r.X*sx),(int)Math.Round(r.Y*sy),(int)Math.Round(r.Width*sx),(int)Math.Round(r.Height*sy));}
                foreach(KeyValuePair<Control,float> kv in viewFonts){float size=kv.Value*k;if(Math.Abs(kv.Key.Font.SizeInPoints*96/72-size)>.1f){Font old=kv.Key.Font;kv.Key.Font=new Font(old.FontFamily,size,old.Style,GraphicsUnit.Pixel);}}
                content.ResumeLayout(true);
                using(System.Drawing.Drawing2D.GraphicsPath p=MwStyle.Round(new RectangleF(0,0,Width,Height),24*k)){Region previous=Region;Region=new Region(p);if(previous!=null)previous.Dispose();}
                Invalidate(true);
            } finally{layingOut=false;}
        }
    }
}
