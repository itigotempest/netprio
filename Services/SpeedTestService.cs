using System.Diagnostics;

namespace NetPrio.Services;

public class SpeedTestService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<double> MeasureDownloadMbpsAsync(CancellationToken ct = default)
    {
        const string url = "https://speed.cloudflare.com/__down?bytes=25000000";
        var sw = Stopwatch.StartNew();
        long totalBytes = 0;

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[65536];
        while (sw.Elapsed.TotalSeconds < 4.0)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            totalBytes += read;
        }
        sw.Stop();

        return Math.Round((totalBytes * 8.0) / (sw.Elapsed.TotalSeconds * 1_000_000.0), 1);
    }
}
