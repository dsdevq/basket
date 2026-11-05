namespace Basket.Api.Prices.Parsing;

/// <summary>A page came back fine but its content is not what the parser knows how to read.</summary>
public sealed class ParseException(string message) : Exception(message);
