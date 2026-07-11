using SubConvert.Exceptions;

namespace SubConvert.Extensions;

public static class DictionaryExtensions
{
    // 必填项提取，取不到直接抛异常
    public static string GetRequiredString(this Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val?.ToString()))
            return val.ToString()!.Trim();
        
        throw new NodeParseException($"缺失必填字段或为空: {key}");
    }

    public static int GetRequiredInt(this Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && int.TryParse(val?.ToString(), out int result) && result > 0 && result <= 65535)
            return result;
        
        throw new NodeParseException($"缺失必填字段或端口格式无效: {key}");
    }

    // 可选项提取，取不到优雅返回 null
    public static string? GetString(this Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val?.ToString()))
            return val.ToString()!.Trim();
        
        return null;
    }

    public static int? GetInt(this Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && int.TryParse(val?.ToString(), out int result))
            return result;
        
        return null;
    }
    
    public static bool GetBool(this Dictionary<string, object> dict, string key, bool defaultValue = false)
    {
        if (dict.TryGetValue(key, out var val) && bool.TryParse(val?.ToString(), out bool result))
            return result;
        
        return defaultValue;
    }

    public static bool? GetNullableBool(this Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && bool.TryParse(val?.ToString(), out bool result))
            return result;
        
        return null;
    }

    public static List<string>? GetStringList(this Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;

        if (val is List<object> list)
            return list.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
        
        if (val is string str)
            return [str];
            
        return null;
    }

    // 针对 Hysteria2 专属的速率解析优化
    public static int? GetSpeedMbps(this Dictionary<string, object> dict, string key)
    {
        string? str = dict.GetString(key);
        if (str == null) return null;
        
        string digits = new([.. str.Where(char.IsDigit)]);
        return int.TryParse(digits, out int speed) ? speed : null;
    }
}