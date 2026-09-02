namespace BoxForge.Exceptions;

public class BoxForgeConversionException(
    string message,
    Exception innerException)
    : Exception(message, innerException);
