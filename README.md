# Local Trailers

A Jellyfin plugin that automatically downloads YouTube trailers and saves them as local files next to your media.

## How it works

1. A scheduled task scans your library for movies and series that have YouTube trailer URLs in their metadata (added by metadata providers like TMDb).
2. For each item, it downloads the first YouTube trailer using [yt-dlp](https://github.com/yt-dlp/yt-dlp) as an MP4 file.
3. The trailer is saved into a `trailers/` subfolder next to the media file, which is where Jellyfin looks for local trailers.

The task runs daily at 3 AM by default and can also be triggered manually from the Jellyfin dashboard under Scheduled Tasks.

## Installation

### From plugin repository

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add a new repository with this URL:
    ```
    https://raw.githubusercontent.com/KartoffelChipss/jellyfin-trailer-plugin/main/manifest.json
    ```
3. Go to **Catalog**, find **Local Trailers**, and install it
4. Restart Jellyfin

### Manual installation

1. Download the latest `.zip` from the [Releases](https://github.com/KartoffelChipss/jellyfin-trailer-plugin/releases) page
2. Extract it into your Jellyfin plugins directory (e.g. `/config/plugins/LocalTrailers/`)
3. Restart Jellyfin

## Requirements

- Jellyfin 10.11+
- yt-dlp (auto-managed by default, or provide your own)
- ffmpeg (bundled with Jellyfin, or provide your own)

## Development

Requires [Task](https://taskfile.dev/) and Docker.

```sh
task up        # Build plugin + start Jellyfin at http://localhost:8096
task restart   # Rebuild plugin + restart Jellyfin
task logs      # Tail container logs
task down      # Stop Jellyfin
task clean     # Stop + remove volumes and build artifacts
```

## License

This project is licensed under the GNU General Public License v3.0 - see the [LICENSE](./LICENSE) file for details.
