using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClassPixelPro.Models.External.ClassWidgets;

public class CwWidgetsProfile
{
    [JsonPropertyName("widgets")]
    public List<string> Widgets { get; set; } = [];
}