using llcom.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace llcom.Pages
{
    /// <summary>
    /// DataShowPage.xaml 的交互逻辑
    /// </summary>
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public partial class DataShowPage : Page
    {
        public DataShowPage()
        {
            InitializeComponent();
        }

        ScrollViewer sv;
        /// <summary>
        /// 禁止自动滚动？
        /// </summary>
        public bool LockLog { get; set; } = false;
        private bool loaded = false;
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (loaded)
                return;
            loaded = true;
            //使日志富文本区域滚动可控制
            sv = uartDataFlowDocument.Template.FindName("PART_ContentHost", uartDataFlowDocument) as ScrollViewer;
            sv.CanContentScroll = true;
            //添加待显示数据到缓冲区
            Tools.Logger.DataShowTask += DataShowAdd;
            //显示数据的任务
            new Thread(DataShowTask).Start();
            Tools.Logger.DataClearEvent += (xx,x) =>
            {
                uartDataFlowDocument.Document.Blocks.Clear();
            };
            LockIcon.DataContext = this;
            UnLockIcon.DataContext = this;
            UnLockText.DataContext = this;
            RTSCheckBox.DataContext = this;
            DTRCheckBox.DataContext = this;
            Rts = false;
            Dtr = true;
            HEXBox.DataContext = Tools.Global.setting;
            HexSendCheckBox.DataContext = Tools.Global.setting;
            this.ExtraEnterCheckBox.DataContext = Tools.Global.setting;
            DisableLogCheckBox.DataContext = Tools.Global.setting;
            EnableSymbolCheckBox.DataContext = Tools.Global.setting;

            packLengthWarn = TryFindResource("SettingMaxShowPackWarn") as string ?? "?!";
            logAutoClearWarn = TryFindResource("SettingMaxPacksWarn") as string ?? "?!";
            packsTooMuch = TryFindResource("BuffPacksTooMuchWarn") as string ?? "?!";
            uiTooLag = TryFindResource("LogLagWarn") as string ?? "?!";
        }

        private bool DoInvoke(Action action)
        {
            if (Tools.Global.isMainWindowsClosed)
                return false;
            Dispatcher.Invoke(action);
            return true;
        }

        /// <summary>
        /// 数据缓冲
        /// </summary>
        private List<Tools.DataShow> DataQueue = new List<Tools.DataShow>();
        /// <summary>
        /// 消息来的信号量
        /// </summary>
        private EventWaitHandle waitQueue = new AutoResetEvent(false);
        /// <summary>
        /// 添加一个日志数据到缓冲区
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DataShowAdd(object sender, Tools.DataShow e)
        {
            lock (DataQueue)
            {
                if(e is DataShowRaw)
                    Logger.AddUartLogInfo($"[{e.time}]{(e as Tools.DataShowRaw).title}\r\n" +
                        $"{Global.GetEncoding().GetString(e.data)}\r\n" +
                        $"HEX:{Tools.Global.Byte2Hex(e.data, " ")}");
                if(DataQueue.Count > 100)
                {
                    DataQueue.Clear();
                    DataQueue.Add(new DataShowRaw
                    {
                        title = packsTooMuch
                    });
                }
                else
                    DataQueue.Add(e);
            }
            waitQueue.Set();
        }
        /// <summary>
        /// 分发显示数据的任务
        /// </summary>
        private void DataShowTask()
        {
            waitQueue.Reset();
            Tools.Global.ProgramClosedEvent += (_, _) =>
            {
                waitQueue.Set();
            };
            while (true)
            {
                waitQueue.WaitOne();
                if (Tools.Global.isMainWindowsClosed)
                    return;
                var logList = new List<Tools.DataShow>();
                lock (DataQueue)//取数据
                {
                    for(int i=0; i<DataQueue.Count;i++)
                        logList.Add(DataQueue[i]);
                    DataQueue.Clear();
                }
                waitQueue.Reset();

                if (logList.Count == 0)//没数据，切走
                    continue;

                //缓存处理好的数据
                var rawList = new List<DataRaw>();
                DateTime uartSentTime = DateTime.MinValue;
                var uartSentList = new List<byte>();
                DateTime uartReceivedTime = DateTime.MinValue;
                var uartReceivedList = new List<byte>();
                for (int i = 0; i < logList.Count; i++)
                {
                    if (logList[i] as Tools.DataShowRaw != null)
                        rawList.Add(new DataRaw(logList[i] as Tools.DataShowRaw));
                    else
                    {
                        //串口数据收发分一下，后续可以合并数据
                        var d = logList[i] as Tools.DataShowPara;
                        if (d.data.Length > 0)
                        {
                            if(d.send)
                            {
                                if(Tools.Global.setting.showSend)//显示发送出去的串口数据
                                {
                                    uartSentList.AddRange(d.data);
                                    if (uartSentTime == DateTime.MinValue)//第一包的时间
                                        uartSentTime = d.time;
                                }
                            }
                            else
                            {
                                uartReceivedList.AddRange(d.data);
                                if (uartReceivedTime == DateTime.MinValue)//第一包的时间
                                    uartReceivedTime = d.time;
                            }
                        }
                    }
                }

                //合并串口数据
                var uartList = new List<DataUart>();
                DataUart sentData = null;
                DataUart receivedData = null;
                if (uartSentList.Count > 0)
                    sentData = new DataUart(uartSentList, uartSentTime, true);
                if (uartReceivedList.Count > 0)
                    receivedData = new DataUart(uartReceivedList, uartReceivedTime, false);
                //包的时间顺序要对
                if (sentData == null && receivedData != null)
                    uartList.Add(receivedData);
                else if (sentData != null && receivedData == null)
                    uartList.Add(sentData);
                else if (sentData != null && receivedData != null)
                {
                    if (uartSentTime < uartReceivedTime)
                    {
                        uartList.Add(sentData);
                        uartList.Add(receivedData);
                    }
                    else
                    {
                        uartList.Add(receivedData);
                        uartList.Add(sentData);
                    }
                }

                //显示数据
                if (rawList.Count == 0 && uartList.Count == 0)
                    continue;
                //记录一下开始的时间
                var start = DateTime.Now;
                if(!DoInvoke(() =>
                {
                    //条目过多，自动清空
                    if (uartDataFlowDocument.Document.Blocks.Count > Tools.Global.setting.MaxPacksAutoClear)
                    {
                        uartDataFlowDocument.Document.Blocks.Clear();
                        Paragraph p = new Paragraph(new Run(logAutoClearWarn));
                        uartDataFlowDocument.Document.Blocks.Add(p);
                    }
                    //禁止选中
                    if (!uartDataFlowDocument.Selection.IsEmpty)
                    {
                        uartDataFlowDocument.IsSelectionEnabled = false;
                        uartDataFlowDocument.IsSelectionEnabled = true;
                    }
                    for (int i = 0; i < rawList.Count; i++)
                        DataShowRaw(rawList[i]);
                    for (int i = 0; i < uartList.Count; i++)
                        addUartLog(uartList[i]);
                    if (!LockLog)//如果允许拉到最下面
                        DoInvoke(sv.ScrollToBottom);
                }))
                    return;
                //如果卡顿超过了半秒，则触发自动清空
                if(Tools.Global.setting.LagAutoClear && (DateTime.Now - start).Milliseconds > 250)
                {
                    DoInvoke(() =>
                    {
                        uartDataFlowDocument.Document.Blocks.Clear();
                        Paragraph p = new Paragraph(new Run(uiTooLag));
                        uartDataFlowDocument.Document.Blocks.Add(p);
                    });
                }
                //正常就延时10ms，防止卡住ui线程
                Thread.Sleep(10);
            }
        }

        private static string packLengthWarn = "";
        private static string logAutoClearWarn = "";
        private static string packsTooMuch = "";
        private static string uiTooLag = "";

        class DataRaw
        {
            public string time;
            public string title;
            public string data = null;
            public string hex = null;
            public SolidColorBrush color;
            public DataRaw(Tools.DataShowRaw d) 
            {
                time = d.time.ToString("[yyyy/MM/dd HH:mm:ss.fff]");
                title = d.title;
                if (d.data != null && d.data.Length > 0)
                {
                    var len = d.data.Length;
                    var warn = "";
                    if (d.data.Length > Tools.Global.setting.MaxPackShow)
                    {
                        warn = packLengthWarn;
                        len = Tools.Global.setting.MaxPackShow;
                    }

                    //主要数据
                    data = Tools.Global.setting.showHexFormat switch
                    {
                        2 => Tools.Global.Byte2Hex(d.data, " ",len) + warn,
                        _ => Tools.Global.Byte2Readable(d.data, len) + warn,
                    };
                    color = d.color;
                    //小字hex
                    if (Tools.Global.setting.showHexFormat == 0)
                        hex = "HEX:" + Tools.Global.Byte2Hex(d.data, " ", len) + warn;
                }
            }
        }
        private void DataShowRaw(DataRaw e)
        {
            Paragraph p = new Paragraph(new Run(""));
            Span text = new Span(new Run(e.time));
            text.Foreground = Brushes.DarkSlateGray;
            p.Inlines.Add(text);
            text = new Span(new Run(e.title));
            text.Foreground = Brushes.Black;
            text.FontWeight = FontWeights.Bold;
            p.Inlines.Add(text);
            uartDataFlowDocument.Document.Blocks.Add(p);

            if (e.data != null)//有数据时才显示信息
            {
                //主要显示数据
                p = new Paragraph(new Run(""));
                text = new Span(new Run(e.data));
                text.Foreground = e.color;
                text.FontSize = 15;
                p.Inlines.Add(text);
                uartDataFlowDocument.Document.Blocks.Add(p);

                //同时显示模式时，才显示小字hex
                if (e.hex != null)
                {
                    p = new Paragraph(new Run(e.hex));
                    p.Foreground = e.color;
                    p.Margin = new Thickness(0, 0, 0, 8);
                    uartDataFlowDocument.Document.Blocks.Add(p);
                }
            }
        }

        class DataUart
        {
            public string time;
            public string title;
            public string data;
            public string hex = null;
            public SolidColorBrush color;
            public SolidColorBrush hexColor;

            public DataUart(List<byte> data, DateTime time, bool sent)
            {
                if (data == null || data.Count == 0)
                    return;
                byte[] temp = data.ToArray();
                //转换下接收数据
                if (!sent)
                {
                    try
                    {
                        temp = LuaEnv.LuaLoader.Run(
                            $"{Tools.Global.setting.recvScript}.lua",
                            new System.Collections.ArrayList { "uartData", temp },
                            "user_script_recv_convert/");
                    }
                    catch (Exception ex)
                    {
                        Tools.MessageBox.Show($"receive convert lua script error\r\n" + ex.ToString());
                        return;
                    }
                    if (temp == null)
                        return;
                }

                this.time = time.ToString("[yyyy/MM/dd HH:mm:ss.fff]");
                title = sent ? " ← " : " → ";
                color = sent ? Brushes.DarkRed : Brushes.DarkGreen;
                hexColor = sent ? Brushes.IndianRed : Brushes.ForestGreen;

                var len = temp.Length;
                var warn = "";
                if (temp.Length > Tools.Global.setting.MaxPackShow)
                {
                    warn = packLengthWarn;
                    len = Tools.Global.setting.MaxPackShow;
                }
                //主要数据
                if(temp != null && temp.Length > 0)
                {
                    this.data = Tools.Global.setting.showHexFormat switch
                    {
                        2 => Tools.Global.Byte2Hex(temp, " ", len) + warn,
                        _ => Tools.Global.Byte2Readable(temp, len) + warn,
                    };
                }
                //同时显示模式时，才显示小字hex
                if (Tools.Global.setting.showHexFormat == 0)
                    hex = "HEX:" + Tools.Global.Byte2Hex(temp, " ", len) + warn;
            }
        }

        /// <summary>
        /// 添加串口日志数据
        /// </summary>
        /// <param name="data">数据</param>
        /// <param name="send">true为发送，false为接收</param>
        private void addUartLog(DataUart d)
        {
            if (Tools.Global.setting.timeout >= 0)
            {
                Paragraph p = new Paragraph(new Run(""));

                Span text = new Span(new Run(d.time));
                text.Foreground = Brushes.DarkSlateGray;
                p.Inlines.Add(text);

                text = new Span(new Run(d.title));
                text.Foreground = Brushes.Black;
                text.FontWeight = FontWeights.Bold;
                p.Inlines.Add(text);

                //主要显示数据
                text = new Span(new Run(d.data));
                text.Foreground = d.color;
                text.FontSize = 15;
                p.Inlines.Add(text);

                //同时显示模式时，才显示小字hex
                if (d.hex != null)
                    p.Margin = new Thickness(0, 0, 0, 8);
                uartDataFlowDocument.Document.Blocks.Add(p);

                //同时显示模式时，才显示小字hex
                if (d.hex != null)
                {
                    p = new Paragraph(new Run(d.hex));
                    p.Foreground = d.hexColor;
                    p.Margin = new Thickness(0, 0, 0, 8);
                    uartDataFlowDocument.Document.Blocks.Add(p);
                }
            }
            else//不分包
            {
                if(uartDataFlowDocument.Document.Blocks.LastBlock == null ||
                   uartDataFlowDocument.Document.Blocks.LastBlock.GetType() != typeof(Paragraph))
                    uartDataFlowDocument.Document.Blocks.Add(new Paragraph(new Run("")));

                //待显示的数据
                string s;
                if (Tools.Global.setting.showHexFormat == 2 && d.hex != null)
                    s = d.hex;
                else
                    s = d.data;
                Span text = new Span(new Run(s));
                text.FontSize = 15;
                text.Foreground = d.color;
                (uartDataFlowDocument.Document.Blocks.LastBlock as Paragraph).Inlines.Add(text);
            }
        }

        private void LockLogButton_Click(object sender, RoutedEventArgs e)
        {
            LockLog = !LockLog;
        }


        public bool Rts {
            get
            {
                return Tools.Global.uart.Rts;
            }
            set
            {
                Tools.Global.uart.Rts = value;
            }
        }
        public bool Dtr
        {
            get
            {
                return Tools.Global.uart.Dtr;
            }
            set
            {
                Tools.Global.uart.Dtr = value;
            }
        }
    }
}
