namespace HappAccessible.Services;

public sealed class AppUpdateCoordinator
{
    private readonly AppUpdateService _updates = new();

    public Task<AppReleaseInfo> CheckAsync(CancellationToken ct = default) =>
        _updates.CheckAsync(ct);

    public async Task ApplyAsync(AppReleaseInfo info, bool silent, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(info.PortableZipUrl) && AppUpdateService.CanSelfUpdateInPlace())
        {
            var script = await _updates.PreparePortableUpdateAsync(info, progress, ct).ConfigureAwait(false);
            SessionJournalService.Record($"Обновление portable → {info.LatestVersion}.");
            AppUpdateService.LaunchUpdaterScript(script);
            return;
        }

        if (!string.IsNullOrEmpty(info.SetupExeUrl))
        {
            var setup = await _updates.DownloadSetupAsync(info, progress, ct).ConfigureAwait(false);
            SessionJournalService.Record($"Тихое обновление setup → {info.LatestVersion}.");
            AppUpdateService.LaunchSetupSilent(setup);
            return;
        }

        if (!string.IsNullOrEmpty(info.PortableZipUrl))
            throw new InvalidOperationException(
                "Папка приложения недоступна для записи. Скачайте portable zip вручную: " + info.ReleaseUrl);

        throw new InvalidOperationException(
            "Обновление недоступно. Скачайте вручную: " + info.ReleaseUrl);
    }
}
