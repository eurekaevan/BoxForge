using System.Text;
using Microsoft.Extensions.Logging;
using SubConvert.Models;

namespace SubConvert.Ui;

public interface IUserInterface
{
    string RequireInput(string envVar, string prompt, bool secret = false);
    TargetPlatform SelectPlatform();
    int SelectAirport(IReadOnlyList<ConfigSourceItem> files);
}

public class ConsoleUi(ILogger<ConsoleUi> logger) : IUserInterface
{
    public string RequireInput(string envVar, string prompt, bool secret = false)
    {
        string? value = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(value))
        {
            logger.LogInformation("已从环境变量 {EnvVar} 读取配置。", envVar);
            return value.Trim();
        }

        Console.Write(prompt);
        if (secret)
        {
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

        return Console.ReadLine()?.Trim() ?? "";
    }

    public TargetPlatform SelectPlatform()
    {
        Console.WriteLine("\n请选择要生成配置的平台：");
        Console.WriteLine("1. Windows");
        Console.WriteLine("2. Android");
        Console.WriteLine("3. Linux");
        Console.Write("请输入选项 (1/2/3): ");
        
        TargetPlatform platform = Console.ReadLine()?.Trim() switch
        {
            "2" => TargetPlatform.Android,
            "3" => TargetPlatform.Linux,
            _ => TargetPlatform.Windows
        };
        logger.LogInformation("目标平台：{Platform}", platform);
        return platform;
    }

    public int SelectAirport(IReadOnlyList<ConfigSourceItem> files)
    {
        Console.WriteLine("\n请选择要处理的机场配置：");
        for (int i = 0; i < files.Count; i++)
            Console.WriteLine($"  {i + 1}. {files[i].DisplayName}");
        Console.WriteLine($"  {files.Count + 1}. 以上所有（批量转换并上传至 GitHub）");
        Console.Write($"\n请输入选项 (1-{files.Count + 1}): ");

        if (!int.TryParse(Console.ReadLine()?.Trim(), out int selection)
            || selection < 1
            || selection > files.Count + 1)
        {
            return -1;
        }
        return selection;
    }
}
