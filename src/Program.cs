using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;

namespace TTPlayer.Lyric.Server
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateSlimBuilder(args);
            if (!args.Contains("--urls")) builder.WebHost.UseUrls("http://*:25168");

            builder.Services.AddSingleton<LyricProvider>();

            var app = builder.Build();

            app.MapGet("/lyric/{source}", async (HttpContext httpContext, [FromRoute] string source, [FromServices] LyricProvider lyricProvider, ILogger<Program> logger) =>
            {
                if (!Enum.TryParse<Searchers>(source, true, out var searcherSource))
                {
                    return Results.NotFound();
                }

                var queryCollection = httpContext.Request.Query;
                string artist = "";
                string title = "";
                string flags = "";
                string lrcId = "";

                if (queryCollection.TryGetValue("sh?artist", out var _artist)) artist = DecodeUTF16Hex(_artist.ToString());
                if (queryCollection.TryGetValue("title", out var _title)) title = DecodeUTF16Hex(_title.ToString());
                if (queryCollection.TryGetValue("flags", out var _flags)) flags = _flags.ToString();
                if (queryCollection.TryGetValue("dl?Id", out var _lrcId)) lrcId = _lrcId.ToString();

                if (string.IsNullOrEmpty(lrcId))
                {
                    var lyrics = await lyricProvider.Search(searcherSource, title, artist);
                    var xmlDocument = new XmlDocument();
                    var result = xmlDocument.CreateElement("result");
                    xmlDocument.AppendChild(result);
                    for (int i = 0; i < lyrics.Count; i++)
                    {
                        var lrc = xmlDocument.CreateElement("lrc");
                        result.AppendChild(lrc);
                        lrc.SetAttribute("id", $"{lyrics[i].Id}");
                        lrc.SetAttribute("artist", $"{lyrics[i].Artist}");
                        lrc.SetAttribute("title", $"{lyrics[i].Title}");
                        lrc.SetAttribute("album", $"{lyrics[i].Album}");
                    }

                    return Results.Content(xmlDocument.OuterXml, "text/xml", Encoding.UTF8, 200);
                }
                else
                {

                    var lyric = await lyricProvider.FindByIdAsync(lrcId);
                    return Results.Content(lyric, "text/plain", Encoding.UTF8);
                }
            });

            app.Run();
        }

        private static string DecodeUTF16Hex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return string.Empty;

            try
            {
                var bytes = Convert.FromHexString(hex);
                return Encoding.Unicode.GetString(bytes);
            }
            catch { }

            return string.Empty;
        }
    }

    public record class LyricResult(int Id, string? PlatformId, Searchers Source, string? Artist, string Title, string? Album);

    internal class LyricProvider
    {
        private string? lastSearchKey;
        private IReadOnlyList<LyricResult>? lastSearchResult;
        private readonly ILogger<LyricProvider> logger;

        public LyricProvider(ILogger<LyricProvider> logger)
        {
            this.logger = logger;
        }

        public async Task<IReadOnlyList<LyricResult>> Search(Searchers source, string title, string artist)
        {
            if (string.IsNullOrEmpty(title)) return [];

            var cacheKey = $"{source}_{title}_{artist}";

            if (lastSearchKey == cacheKey && lastSearchResult != null) return lastSearchResult;
            ISearcher? searcher = source switch
            {
                Searchers.QQMusic => new QQMusicSearcher(),
                Searchers.Netease => new NeteaseSearcher(),
                _ => null
            };

            if (searcher == null) return [];

            var results = await searcher.SearchForResults(new TrackMultiArtistMetadata()
            {
                Title = title,
                Artist = artist,
            });

            if (results != null && results.Count > 0)
            {
                var list = new List<LyricResult>(results.Count);
                for (int i = 0; i < results.Count; i++)
                {
                    var platformId = results[i] switch
                    {
                        NeteaseSearchResult neteaseSearchResult => neteaseSearchResult.Id,
                        QQMusicSearchResult qqMusicSearchResult => qqMusicSearchResult.Mid,
                        _ => null
                    };

                    var value = new LyricResult(i + 10000, platformId, source, results[i].Artist, results[i].Title, results[i].Album);
                    list.Add(value);
                }

                lastSearchKey = cacheKey;
                lastSearchResult = list;
            }
            else
            {
                lastSearchKey = null;
                lastSearchResult = null;
            }

            return lastSearchResult ?? [];
        }

        public async Task<string?> FindByIdAsync(string id)
        {
            var list = lastSearchResult;

            if (list != null && int.TryParse(id, out var id2))
            {
                var id3 = id2 - 10000;
                if (id3 >= 0 && id3 < list.Count)
                {
                    var result = list[id3];
                    if (result.Source == Searchers.Netease)
                    {
                        var lyric = await ProviderHelper.NeteaseApi.GetLyric(result.PlatformId!);
                        if (lyric != null)
                        {
                            return lyric.Lrc.Lyric;
                        }
                    }
                    else if (result.Source == Searchers.QQMusic)
                    {
                        var lyric = await ProviderHelper.QQMusicApi.GetLyric(result.PlatformId!);
                        if (lyric != null)
                        {
                            return lyric.Lyric;
                        }
                    }
                }
            }

            return null;
        }
    }
}
