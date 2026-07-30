using System.Text;
using Microsoft.Extensions.Logging;
using BoxForge.Models;

namespace BoxForge.Ui;

public interface IUserInterface
{
    void ShowBanner();
    string RequireInput(string envVar, string prompt, bool secret = false);
    TargetPlatform SelectPlatform();
    int SelectAirport(IReadOnlyList<ConfigSourceItem> files);
}

public class ConsoleUi(ILogger<ConsoleUi> logger) : IUserInterface
{
    private static bool ColorsEnabled => !Console.IsOutputRedirected;

    public void ShowBanner()
    {
        WriteLine();
        WriteLine("╭────────────────────────────────────────────╮", ConsoleColor.DarkCyan);
        Write("│  ", ConsoleColor.DarkCyan);
        Write("BoxForge", ConsoleColor.Cyan);
        WriteLine("                                │", ConsoleColor.DarkCyan);
        Write("│  ", ConsoleColor.DarkCyan);
        Write("Clash YAML", ConsoleColor.Gray);
        Write("  →  ", ConsoleColor.DarkGray);
        Write("sing-box config.json", ConsoleColor.Gray);
        WriteLine("       │", ConsoleColor.DarkCyan);
        WriteLine("╰────────────────────────────────────────────╯", ConsoleColor.DarkCyan);
        WriteLine();
    }

    public string RequireInput(string envVar, string prompt, bool secret = false)
    {
        string? value = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(value))
        {
            logger.LogInformation("已从环境变量 {EnvVar} 读取配置。", envVar);
            return value.Trim();
        }

        Write("› ", ConsoleColor.Cyan);
        Write(prompt, ConsoleColor.Gray);
        if (secret)
        {
            if (Console.IsInputRedirected)
                return ReadRedirectedInput();

            var sb = new StringBuilder();
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                    sb.Remove(sb.Length - 1, 1);
                else if (key.Key != ConsoleKey.Backspace)
                    sb.Append(key.KeyChar);
            }
            Console.WriteLine();
            return sb.ToString().Trim();
        }

        if (Console.IsInputRedirected)
            return ReadRedirectedInput();

        return Console.ReadLine()?.Trim() ?? "";
    }

    public TargetPlatform SelectPlatform()
    {
        WriteSection("目标平台");
        WriteMenuItem(1, "Windows", "默认");
        WriteMenuItem(2, "Android");
        WriteMenuItem(3, "Linux");
        WritePrompt("请选择 [1-3]，直接回车使用 Windows");

        TargetPlatform platform = Console.ReadLine()?.Trim() switch
        {
            "2" => TargetPlatform.Android,
            "3" => TargetPlatform.Linux,
            _ => TargetPlatform.Windows
        };

        WriteSelection(platform.ToString());
        return platform;
    }

    public int SelectAirport(IReadOnlyList<ConfigSourceItem> files)
    {
        WriteSection($"配置来源 · {files.Count} 个");
        for (int i = 0; i < files.Count; i++)
            WriteMenuItem(i + 1, files[i].DisplayName);
        WriteMenuItem(files.Count + 1, "全部配置", "批量转换并上传");
        WritePrompt($"请选择 [1-{files.Count + 1}]");

        if (!int.TryParse(Console.ReadLine()?.Trim(), out int selection)
            || selection < 1
            || selection > files.Count + 1)
        {
            return -1;
        }

        string selected = selection == files.Count + 1
            ? "全部配置"
            : files[selection - 1].DisplayName;
        WriteSelection(selected);
        return selection;
    }

    private static void WriteSection(string title)
    {
        WriteLine();
        Write("┌─ ", ConsoleColor.DarkCyan);
        WriteLine(title, ConsoleColor.Cyan);
    }

    private static void WriteMenuItem(int number, string label, string? hint = null)
    {
        Write("│  ", ConsoleColor.DarkCyan);
        Write($"{number,2}", ConsoleColor.Cyan);
        Write($"  {label}", ConsoleColor.Gray);
        if (!string.IsNullOrWhiteSpace(hint))
            Write($"  · {hint}", ConsoleColor.DarkGray);
        WriteLine();
    }

    private static void WritePrompt(string prompt)
    {
        Write("└─ ", ConsoleColor.DarkCyan);
        Write($"{prompt} › ", ConsoleColor.Gray);
    }

    private static void WriteSelection(string value)
    {
        Write("   ✓ ", ConsoleColor.Green);
        WriteLine($"已选择：{value}", ConsoleColor.DarkGray);
    }

    private static string ReadRedirectedInput()
    {
        string value = Console.ReadLine()?.Trim() ?? "";
        Console.WriteLine();
        return value;
    }

    private static void Write(string value, ConsoleColor color)
    {
        if (!ColorsEnabled)
        {
            Console.Write(value);
            return;
        }

        ConsoleColor previousColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ForegroundColor = previousColor;
    }

    private static void WriteLine(string value = "", ConsoleColor? color = null)
    {
        if (color is null)
        {
            Console.WriteLine(value);
            return;
        }

        Write(value, color.Value);
        Console.WriteLine();
    }
}
