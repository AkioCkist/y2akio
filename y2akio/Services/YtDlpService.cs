using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

public partial class YtDlpService
{
    // Adjust these if yt-dlp.exe / ffmpeg.exe aren't on PATH. Ship them next to the app
    // (e.g. in a "bin" subfolder) and point these at that folder for a self-contained install.
    private readonly string _ytDlpPath;
    private readonly string _ffmpegDirectory;

    // Shared client for the cheap oEmbed metadata call (never spawns yt-dlp).
    private static readonly HttpClient _http = new();

    public YtDlpService(string ytDlpPath = "yt-dlp.exe", string ffmpegDirectory = "")
    {
        _ytDlpPath = ytDlpPath;
        _ffmpegDirectory = ffmpegDirectory;
    }

}
