using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Backroom.EndToEndTests;

/// <summary>JSON calls to the control plane and the cells with a bearer token, as the web app makes them.</summary>
internal static class Api
{
    public static async Task<ApiResponse> SendAsync(
        HttpClient http, HttpMethod method, Uri url, string? token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ApiResponse(response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public static Task<ApiResponse> GetAsync(HttpClient http, Uri url, string? token, CancellationToken cancellationToken) =>
        SendAsync(http, HttpMethod.Get, url, token, body: null, cancellationToken);

    public static Task<ApiResponse> PostAsync(HttpClient http, Uri url, string token, object body, CancellationToken cancellationToken) =>
        SendAsync(http, HttpMethod.Post, url, token, body, cancellationToken);
}
