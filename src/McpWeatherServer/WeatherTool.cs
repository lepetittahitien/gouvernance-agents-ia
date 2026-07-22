using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Http;
using ModelContextProtocol.Server;

namespace McpWeatherServer;

[McpServerToolType]
public static class WeatherTool
{
    [McpServerTool, Description("Donne la météo actuelle (température, vent) pour une ville donnée.")]
    public static async Task<string> GetWeather(
        IHttpClientFactory httpClientFactory,
        [Description("Nom de la ville, ex: Paris, Lyon, Marseille")] string city)
    {
        using var client = httpClientFactory.CreateClient();

        var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=fr&format=json";
        var geoJson = await client.GetStringAsync(geoUrl);
        using var geoDoc = JsonDocument.Parse(geoJson);

        if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return $"Ville introuvable: {city}";
        }

        var place = results[0];
        var lat = place.GetProperty("latitude").GetDouble();
        var lon = place.GetProperty("longitude").GetDouble();
        var placeName = place.GetProperty("name").GetString();

        var weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,wind_speed_10m,relative_humidity_2m";
        var weatherJson = await client.GetStringAsync(weatherUrl);
        using var weatherDoc = JsonDocument.Parse(weatherJson);
        var current = weatherDoc.RootElement.GetProperty("current");

        var temperature = current.GetProperty("temperature_2m").GetDouble();
        var windSpeed = current.GetProperty("wind_speed_10m").GetDouble();
        var humidity = current.GetProperty("relative_humidity_2m").GetDouble();

        return $"Météo à {placeName}: {temperature}°C, vent {windSpeed} km/h, humidité {humidity}%";
    }
}
