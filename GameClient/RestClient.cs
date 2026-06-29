using System.Text;

public class RestClient
{
    private static readonly HttpClient client = new HttpClient();

    public async Task<string> PostDataAsync(string uri, string jsonData)
    {
        var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(uri, content);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadAsStringAsync();
    }
}