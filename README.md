# True Shuffle for Noctis

Version 1.1.1 restores shuffle-button menus on Noctis 1.6.0. The queue adapter accepts the new
optional source/announcement arguments as well as the previous method signature. Menu discovery
keeps retrying if the player is not ready yet. Native playback and menu tests run against the
locally installed Noctis with synthetic tracks and no-op audio.

Four shuffle modes for the current queue, open playlist, or whole library. Built and tested against
Noctis **1.5.9**, plugin kit **1.2**, on Windows. Opening or enabling the plugin does not change playback.
Click **Apply shuffle** to replace the upcoming queue; the current song keeps playing by default.

## Install and use

The [WordLyrics one-line installer](https://github.com/Alfamin/WordLyrics) also installs and updates
True Shuffle when Noctis is present, alongside WordLyrics, Lyric Motion and Free Music Finder. It
preserves plugin data and leaves new plugins disabled until you approve and enable them in Noctis.
Set `WORDLYRICS_NO_EXTRAS=1` before running that installer to skip the optional plugins.

For manual installation, download
[TrueShuffle-for-Noctis.zip](https://github.com/Alfamin/TrueShuffle/raw/main/dist/TrueShuffle-for-Noctis.zip):

1. In Noctis, open **Settings → Plugins → Install from file…** and choose
   `dist\TrueShuffle-for-Noctis.zip`.
2. Enable Community plugins if necessary, enable **True Shuffle**, and approve its playback, library,
   track-menu and notification permissions.
3. Click Noctis's **shuffle button** to choose a mode. The first four choices shuffle the current
   queue; the **Open playlist** and **Whole library** submenus offer the same modes for those sources.
   Picking a mode applies it directly and keeps the current song playing. The active mode is checked
   in the menu and named in the tooltip. **Noctis shuffle (normal)** keeps the original toggle available.
4. For extra options, choose **More shuffle controls…**, or right-click a song → **True Shuffle…**.
   The full window offers **Apply shuffle**, keeping/restarting the current song, and resetting progress.
   You can also open that window from the plugin's settings.

The button menu is attached to the native shuffle command, including matching controls in secondary
windows. Disabling the plugin restores the button's original menu/tooltip and click behavior. If the
shuffle button is hidden in Noctis's playback-bar options, use the song context menu instead.

To hear unfamiliar songs in a playlist, open that playlist and select **Open playlist → Discovery**.
For equal chances across the entire library with remembered progress, select **Whole library → No repeats**.
The playlist source uses the displayed tracks, including its current search filter. A missing playlist
is refused rather than replaced with a broader source. Missing local files are excluded.

| Mode | Ordering | Across later shuffles |
|---|---|---|
| Equal chance | Uniform Fisher–Yates permutation; every eligible song once | A later shuffle can revisit a song |
| No repeats | Uniform among songs not started in the remembered cycle | Excludes previous starts until that source is exhausted |
| Discovery | Lowest Noctis play counts first; random order within ties | Uses the same remembered no-repeat cycle |
| Never played only | Only Noctis play count zero, and not started in a True Shuffle session | Stops when none remain; never falls back to familiar songs |

Skip-when-shuffling, active snoozes and Noctis's explicit-content setting are respected. Disliked songs
have the same chance by default; **Exclude songs marked as disliked** is an optional explicit filter.
Favorites and star ratings do not affect any mode.

## How cycles and playback controls work

- A song counts when it **starts**, including a song you immediately skip or one whose playback fails.
  Noctis itself increments play count on a start, rather than waiting for a completed listen.
- Progress is shared across the plugin's sources and modes and survives app restarts. Queued songs
  that have not started remain eligible. Pressing Apply again does not mark the whole queue as heard.
- When No repeats or Discovery exhausts the eligible tracks in the selected source, the next Apply
  begins a new cycle for that source. Progress for songs outside that source is kept. A boundary repeat
  is avoided when possible; Discovery keeps lower-count tracks first.
- New files become eligible immediately. Different file copies are separate songs; duplicate queue
  entries referring to the same file are deduplicated. Renaming a file gives it a new identity.
- **Reset remembered cycle…** requires a second confirmation and leaves the current queue intact.
  It affects plugin progress, not Noctis's play counts or listening history.
- A custom queue suspends similar-song **Autoplay** and **Repeat** and turns the built-in weighted
  Shuffle off. Starting it ends **Track Radio**. Closing the control window keeps the custom session
  active. The finite queue stops when it runs out; it does not refill automatically.
- **Return to Noctis controls**, changing native shuffle/repeat/radio/autoplay controls, or disabling
  the plugin ends the session. Prior Autoplay and Repeat values are restored unless you changed them.
  The arranged queue is kept, built-in Shuffle stays off unless you turn it on, and the old radio seed
  is not resurrected. After restarting Noctis, use Apply again to continue the remembered cycle; the
  plugin does not silently take over a restored queue when enabled.

Equal chance means statistically uniform selection, which can form clusters. No repeats and Discovery
are separate modes because avoiding past songs or prioritizing unplayed songs deliberately changes
the selection pool/order. A finite library cannot supply unheard songs forever.

## What Noctis normally does

The code in the installed Noctis 1.5.9 uses weighted sampling without replacement: ordinary weight
1.0, disliked weight 0.2, and an additional 0.25 multiplier for recent ids on paths that provide them.
Favorites, ratings and play counts do **not** receive a direct boost. A fresh native shuffle can revisit
songs from earlier queues. Autoplay, once the queue naturally drains, selects matching genres first
and then the same primary artist. This can reduce variety; it does not establish the cause of every
repeat someone hears.

Sources for the exact installed build:

- [ShuffleHelper, commit f8ec553](https://github.com/heartached/Noctis/blob/f8ec5531f9c30807358889a250331350e46bc553/src/Noctis/Helpers/ShuffleHelper.cs)
- [PlayerViewModel](https://github.com/heartached/Noctis/blob/f8ec5531f9c30807358889a250331350e46bc553/src/Noctis/ViewModels/PlayerViewModel.cs)
- [AutoplayService](https://github.com/heartached/Noctis/blob/f8ec5531f9c30807358889a250331350e46bc553/src/Noctis/Services/AutoplayService.cs)

## Storage, permissions and compatibility

The plugin writes only its own `cycle.json`, normally under
`%APPDATA%\Noctis\plugin-data\dev.moshi.trueshuffle\`. It stores hashed file/track identities and the
last identity, not song text or audio. Writes are serialized, staged and flushed before publication.
Corrupt, oversized or linked history is preserved; persistent modes refuse to proceed until the
history is repaired. Equal chance remains available. No audio files, tags, lyrics, playlists, native
play counts, app settings files or credentials are edited by this plugin. Normal playback still
updates Noctis's own statistics as usual. It uses no HTTP client, token, download or telemetry.

Noctis's [plugin API](https://github.com/heartached/Noctis/blob/f8ec5531f9c30807358889a250331350e46bc553/docs/PLUGINS.md)
does not expose queue editing. This plugin therefore uses a narrow **unsupported desktop adapter**:
it reads the current native track objects and calls Noctis's existing queue methods. It checks types,
methods and properties before applying, refuses changed contracts, and does not patch Noctis code.
A future Noctis update may require a plugin update even if the plugin kit remains compatible.
Do not interpret .NET plugin permissions as a sandbox; review the complete source in this project's
`src` folder. The install ZIP contains only the plugin DLL and manifest.

If a native upcoming-queue append fails, the adapter attempts to restore the prior upcoming songs
without restarting the current song. A recovery error is reported. Remembered progress is not reset
merely because queue application failed.

## Build and verification

`build.ps1` uses an existing Roslyn compiler, installed Noctis reference assemblies, existing .NET 10
runtime and cached Avalonia.Headless 12.1.3. It downloads nothing. Paths can be overridden with
`-Compiler`, `-Noctis` and `-Dotnet`. The default paths reflect this development PC.

    & .\build.ps1

The default build runs **84 assertions** before creating the plugin ZIP, covering uniform permutations,
2000-track membership, cycle restart/persistence, eligibility and discovery ordering, failed-append
recovery, cancellation, lifecycle and preference restoration. Headless UI tests exercise the actual
window and plugin entry point with a fake host. Native integration uses the installed Noctis
PlayerViewModel and Track types with fake audio/library/persistence services: it verifies exact queue
order, uninterrupted current playback, Next/natural advance and drain while Autoplay/Repeat were
originally enabled. Tests never instantiate Noctis's real audio service or touch the live music library.
`dist\tests\shuffle-window.png` is the rendered UI preview. No full live-player listening test has been
performed and the package is not automatically installed or enabled.
The tests also exercise the real Avalonia button click path: opening the mode menu does not accidentally
execute the native weighted shuffle command, source submenus apply directly, and disabling restores it.
