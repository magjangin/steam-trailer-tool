using System.Text;
using SteamTrailerTool.Core.Models;

namespace SteamTrailerTool.Core.Services;

public static class FileNameHelper
{
    private static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars()];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Windows에서 파일명으로 쓸 수 없는 문자를 '_'로 바꾸고 예약어·끝 공백/마침표를 정리한다.</summary>
    public static string Sanitize(string name, int maxLength = 150)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd('.', ' ');
        }

        if (result.Length == 0)
        {
            result = "_";
        }

        var stem = result.Split('.')[0];
        return Reserved.Contains(stem) ? "_" + result : result;
    }

    /// <summary>
    /// 트레일러별 파일명 줄기("게임명 - 트레일러명")를 만든다.
    /// 같은 앱 안에서 트레일러명이 겹치면 뒤에 트레일러 ID를 붙여 충돌을 막는다.
    /// </summary>
    public static IReadOnlyDictionary<long, string> BuildStems(AppTrailers app)
    {
        var dupNames = app.Trailers
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<long, string>();
        foreach (var t in app.Trailers)
        {
            var raw = dupNames.Contains(t.Name) ? $"{app.AppName} - {t.Name} ({t.Id})" : $"{app.AppName} - {t.Name}";
            result[t.Id] = Sanitize(raw);
        }

        return result;
    }
}
