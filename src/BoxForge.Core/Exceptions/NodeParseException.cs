namespace BoxForge.Exceptions;

// 轻量级自定义异常，用于打断缺失必填字段的解析流程
public sealed class NodeParseException(string message) : Exception(message);
