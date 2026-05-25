using System.Text.Json.Serialization;

namespace StjBench;

public sealed class StjAddress
{
    [JsonPropertyName("street")] public string Street { get; set; } = "";
    [JsonPropertyName("city")]   public string City   { get; set; } = "";
    [JsonPropertyName("zip")]    public string Zip    { get; set; } = "";
}

public sealed class StjUser
{
    [JsonPropertyName("id")]      public int     Id      { get; set; }
    [JsonPropertyName("name")]    public string  Name    { get; set; } = "";
    [JsonPropertyName("email")]   public string? Email   { get; set; }
    [JsonPropertyName("active")]  public bool    Active  { get; set; }
    [JsonPropertyName("score")]   public double? Score   { get; set; }
    [JsonPropertyName("address")] public StjAddress? Address { get; set; }
    [JsonPropertyName("tags")]    public string[]? Tags  { get; set; }
    [JsonPropertyName("scores")]  public long[]?   Scores { get; set; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(StjUser))]
[JsonSerializable(typeof(StjAddress))]
public partial class StjContext : JsonSerializerContext
{
}
