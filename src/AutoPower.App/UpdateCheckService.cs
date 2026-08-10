using System.Net.Http;
using System.Net.Http.Headers;
using AutoPower.Core;

namespace AutoPower.App;

/// <summary>
/// Periodic GitHub latest-release check. Every failure is swallowed into
/// <see cref="UpdateCheckResult.Failure"/> so networking can never affect app features.
/// </summary>
internal sealed class UpdateCheckService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<string>> _fetchLatestReleaseJson;
    private readonly Version _currentVersion;
    private readonly Action<string, string> _logInformation;
    private readonly CancellationTokenSource _disposal = new();
    private Task? _periodicLoop;

    public UpdateCheckService(
        Version currentVersion,
        Action<string, string> logInformation,
        Func<CancellationToken, Task<string>>? fetchLatestReleaseJson = null)
    {
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _logInformation = logInformation ?? throw new ArgumentNullException(nameof(logInformation));
        _fetchLatestReleaseJson = fetchLatestReleaseJson ?? FetchFromGitHubAsync;
    }

    public UpdateCheckResult? Latest { get; private set; }

    public event Action<UpdateCheckResult>? Updated;

    public void Start()
    {
        _periodicLoop ??= Task.Run(RunPeriodicAsync);
    }

    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
            linked.CancelAfter(RequestTimeout);
            var json = await _fetchLatestReleaseJson(linked.Token).ConfigureAwait(false);
            result = UpdateCheckPolicy.Evaluate(_currentVersion, json);
        }
        catch (Exception error)
        {
            _logInformation("update-check.failed", error.Message);
            result = UpdateCheckResult.Failure;
        }

        Latest = result;
        Updated?.Invoke(result);
        return result;
    }

    public void Dispose()
    {
        _disposal.Cancel();
        _disposal.Dispose();
    }

    private async Task RunPeriodicAsync()
    {
        try
        {
            while (!_disposal.IsCancellationRequested)
            {
                await CheckNowAsync(_disposal.Token).ConfigureAwait(false);
                await Task.Delay(CheckInterval, _disposal.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private static async Task<string> FetchFromGitHubAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("eslee-auto-power", "update-check"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.GetAsync(new Uri(UpdateCheckPolicy.LatestReleaseApiUrl), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
