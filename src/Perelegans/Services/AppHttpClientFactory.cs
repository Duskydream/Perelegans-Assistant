using System.Net.Http;
using Perelegans.Models;

namespace Perelegans.Services;

public static class AppHttpClientFactory
{
    public static HttpClient Create(AppSettings settings)
    {
        return new HttpClient();
    }
}
