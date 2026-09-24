using SmsLink.Protocol;
using System.Drawing.Imaging;

namespace SmsLink;

internal static class Program
{
    private const string MutexName = @"Local\SmsLink-SingleInstance";
    private const string ShowEventName = @"Local\SmsLink-ShowWindow";

    /// <summary>
    /// 支持两个调试参数（正常双击运行时用不到）：
    ///   --data &lt;目录&gt;        指定数据目录，便于用干净配置试界面
    ///   --screenshot &lt;文件&gt;   把窗口离屏渲染成 PNG 后退出
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        var dataDirectory = ReadOption(args, "--data") ?? Path.Combine(AppContext.BaseDirectory, "data");
        var screenshotPath = ReadOption(args, "--screenshot");
        var debugMode = screenshotPath != null;

        // 调试截图时允许和正在运行的实例共存：不抢单实例锁、不占端口。
        if (!isFirstInstance && !debugMode)
        {
            // 已经有一个实例在跑：叫它把窗口显示出来，然后退出自己。
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch
            {
                // 实例刚退出，忽略
            }
            return;
        }

        ApplicationConfiguration.Initialize();

        var config = AppConfig.Load(dataDirectory);

        // 开机自启的勾选状态以注册表为准，避免用户手动删掉注册表后界面还显示已开。
        var registered = MainForm.AutostartRegistered();
        if (registered != config.StartWithWindows)
        {
            config.StartWithWindows = registered;
            config.Save();
        }

        using var showEvent = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, ShowEventName);

        using var form = new MainForm(config, config.LoadMessages(), startServer: !debugMode);

        if (screenshotPath != null)
        {
            form.Shown += (_, _) =>
            {
                var timer = new System.Windows.Forms.Timer { Interval = 700 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    Application.DoEvents();

                    if (args.Contains("--about"))
                    {
                        using var about = new Ui.AboutForm();
                        about.StartPosition = FormStartPosition.Manual;
                        about.Location = new Point(form.Left, form.Top);
                        about.Show();
                        Application.DoEvents();
                        Capture(about, screenshotPath);
                        about.Close();
                        Application.Exit();
                        return;
                    }

                    try
                    {
                        // 固定成默认窗口尺寸再截图：调试实例的 DPI 感知和真实 GUI 进程可能不同，
                        // 否则截图尺寸和用户看到的窗口对不上（曾因此漏掉一个遮挡 bug）。
                        form.ForceScale(1f);
                        Capture(form, screenshotPath);
                        Console.WriteLine($"layout: {form.DescribeLayout()}");
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine($"screenshot failed: {error.Message}");
                    }
                    Application.Exit();
                };
                timer.Start();
            };
        }

        static void Capture(Form target, string path)
        {
            using var bitmap = new Bitmap(target.ClientSize.Width, target.ClientSize.Height);
            target.DrawToBitmap(bitmap, new Rectangle(Point.Empty, target.ClientSize));
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"screenshot: {path} ({target.ClientSize.Width}x{target.ClientSize.Height})");
        }

        var watcher = new Thread(() =>
        {
            while (true)
            {
                showEvent.WaitOne();
                try
                {
                    form.BeginInvoke(() =>
                    {
                        form.Show();
                        form.WindowState = FormWindowState.Normal;
                        form.Activate();
                    });
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
        };
        watcher.Start();

        Application.Run(form);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }
}
