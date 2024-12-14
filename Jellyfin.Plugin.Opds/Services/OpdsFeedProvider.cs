using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Opds.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.Opds.Services;

/// <summary>
/// OPDS feed provider.
/// </summary>
public class OpdsFeedProvider : IOpdsFeedProvider
{
    private static readonly BaseItemKind[] BookItemTypes = { BaseItemKind.Book };
    private static readonly AuthorDto PluginAuthor = new("Jellyfin", "https://github.com/jellyfin/jellyfin-plugin-opds");

    private readonly ILibraryManager _libraryManager;
    private readonly ISearchEngine _searchEngine;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpdsFeedProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="searchEngine">Instance of the <see cref="ISearchEngine"/> interface.</param>
    /// <param name="serverApplicationHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public OpdsFeedProvider(
        ILibraryManager libraryManager,
        ISearchEngine searchEngine,
        IServerApplicationHost serverApplicationHost,
        IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _searchEngine = searchEngine;
        _serverApplicationHost = serverApplicationHost;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public FeedDto GetFeeds(string baseUrl)
    {
        var timestamp = DateTime.UtcNow;
        return new FeedDto
        {
            Id = Guid.NewGuid().ToString(),
            Links = new[]
            {
                new LinkDto("self", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;kind=navigation"),
                new LinkDto("start", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;kind=navigation", "Start"),
                new LinkDto("search", baseUrl + "/opds/osd", "application/opensearchdescription+xml"),
                new LinkDto("search", baseUrl + "/opds/search/{searchTerms}", "application/atom+xml", "Search")
            },
            Title = GetFeedName("Feeds"),
            Author = PluginAuthor,
            Entries = new List<EntryDto>
            {
                new(
                    "Authors",
                    "/opds/authors",
                    new ContentDto("text", "Browse books by author"),
                    timestamp)
                {
                    Links = new List<LinkDto>
                    {
                        new(baseUrl + "/opds/authors", "application/atom+xml;profile=opds-catalog")
                    }
                },
                new(
                    "Favorite Books",
                    "/opds/books/favorite",
                    new ContentDto("text", "Favorite books"),
                    timestamp)
                {
                    Links = new List<LinkDto>
                    {
                        new(baseUrl + "/opds/books/favorite", "application/atom+xml;profile=opds-catalog")
                    }
                }
            }
        };
    }

    /// <inheritdoc />
    public FeedDto GetAuthors(string baseUrl, Guid userId)
    {
        var feedDto = new FeedDto
        {
            Id = Guid.NewGuid().ToString(),
            Author = PluginAuthor,
            Title = GetFeedName("Authors"),
            Links = new[]
            {
                new LinkDto("self", baseUrl + "/opds/authors?", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("start", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("up", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("search", baseUrl + "/opds/osd", "application/opensearchdescription+xml"),
                new LinkDto("search", baseUrl + "/opds/search/{searchTerms}", "application/atom+xml", "Search")
            },
            Entries = new List<EntryDto>()
        };

        // Add alphabetical entries
        var utcNow = DateTime.UtcNow;
        feedDto.Entries.Add(new EntryDto(
            "All Authors",
            "/opds/authors/all",
            utcNow)
        {
            Links = new List<LinkDto>
            {
                new(
                    "subsection",
                    baseUrl + "/opds/authors/all",
                    "application/atom+xml;profile=opds-catalog")
            }
        });

        for (var i = 'A'; i <= 'Z'; i++)
        {
            var letter = char.ToString(i);
            feedDto.Entries.Add(new EntryDto(
                letter,
                "/opds/authors/letter/" + letter,
                utcNow)
            {
                Links = new List<LinkDto>
                {
                    new(
                        "subsection",
                        baseUrl + "/opds/authors/letter/" + letter,
                        "application/atom+xml;profile=opds-catalog")
                }
            });
        }

        return feedDto;
    }

    /// <inheritdoc />
    public FeedDto GetAuthorsByLetter(string baseUrl, Guid userId, string letter)
    {
        var feedDto = new FeedDto
        {
            Id = Guid.NewGuid().ToString(),
            Author = PluginAuthor,
            Title = GetFeedName(letter == "all" ? "All Authors" : $"Authors - {letter}"),
            Links = new[]
            {
                new LinkDto("self", baseUrl + "/opds/authors/" + letter, "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("start", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("up", baseUrl + "/opds/authors", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("search", baseUrl + "/opds/osd", "application/opensearchdescription+xml"),
                new LinkDto("search", baseUrl + "/opds/search/{searchTerms}", "application/atom+xml", "Search")
            },
            Entries = new List<EntryDto>()
        };

        // Configure query to get all books
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = BookItemTypes,
            Recursive = true,
            EnableTotalRecordCount = true
        };

        if (userId != Guid.Empty)
        {
            var user = _userManager.GetUserById(userId);
            if (user is not null)
            {
                query.SetUser(user);
            }
        }

        var utcNow = DateTime.UtcNow;
        var queryResult = _libraryManager.GetItemsResult(query);

        // Debug logging
        System.Console.WriteLine($"Found {queryResult.TotalRecordCount} total books");

        // Get unique authors from books using a HashSet to avoid duplicates
        var authorNames = new HashSet<string>();
        var authorIds = new Dictionary<string, Guid>();

        foreach (var item in queryResult.Items)
        {
            if (item is Book book)
            {
                // Debug logging
                System.Console.WriteLine($"\nBook: {book.Name}");
                System.Console.WriteLine($"Path: {book.Path}");

                // Try to extract author from path
                // Expected format: /media/share/books/Calibre/Author Name/Book Title/...
                var pathParts = book.Path.Split('/');
                if (pathParts.Length >= 6) // Make sure we have enough parts
                {
                    var authorFromPath = pathParts[5]; // Index 5 should be the author folder name
                    if (!string.IsNullOrEmpty(authorFromPath))
                    {
                        // Convert to "Last, First" format if possible
                        var authorParts = authorFromPath.Split(' ');
                        var authorName = authorFromPath;
                        if (authorParts.Length > 1)
                        {
                            var lastName = authorParts[^1]; // Last element
                            var firstName = string.Join(" ", authorParts.Take(authorParts.Length - 1));
                            authorName = $"{lastName}, {firstName}";
                        }

                        System.Console.WriteLine($"Found author from path: {authorName}");
                        authorNames.Add(authorName);
                        if (!authorIds.ContainsKey(authorName))
                        {
                            authorIds[authorName] = GetStableGuid(authorName);
                        }
                    }
                }
            }
        }

        // Debug logging
        System.Console.WriteLine($"\nFound {authorNames.Count} unique authors");
        foreach (var name in authorNames)
        {
            System.Console.WriteLine($"Author: {name}");
        }

        // Convert to sorted list and filter by letter if needed
        var sortedAuthors = authorNames.OrderBy(name => name);
        if (letter != "all")
        {
            sortedAuthors = sortedAuthors.Where(name => name.StartsWith(letter, StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(name => name);
        }

        foreach (var authorName in sortedAuthors)
        {
            feedDto.Entries.Add(new EntryDto(
                authorName,
                "/opds/authors/" + authorIds[authorName],
                utcNow)
            {
                Links = new List<LinkDto>
                {
                    new(
                        "subsection",
                        baseUrl + "/opds/authors/" + authorIds[authorName],
                        "application/atom+xml;profile=opds-catalog")
                }
            });
        }

        return feedDto;
    }

    /// <inheritdoc />
    public FeedDto GetBooksByAuthor(string baseUrl, Guid userId, Guid authorId)
    {
        var feedDto = new FeedDto
        {
            Id = Guid.NewGuid().ToString(),
            Author = PluginAuthor,
            Title = GetFeedName("Books by Author"),
            Links = new[]
            {
                new LinkDto("self", baseUrl + "/opds/authors/" + authorId, "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("start", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("up", baseUrl + "/opds/authors", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("search", baseUrl + "/opds/osd", "application/opensearchdescription+xml"),
                new LinkDto("search", baseUrl + "/opds/search/{searchTerms}", "application/atom+xml", "Search")
            },
            Entries = new List<EntryDto>()
        };

        // Configure query to get all books
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = BookItemTypes,
            Recursive = true,
            EnableTotalRecordCount = true
        };

        if (userId != Guid.Empty)
        {
            var user = _userManager.GetUserById(userId);
            if (user is not null)
            {
                query.SetUser(user);
            }
        }

        // Get all books and filter for those by the requested author
        var queryResult = _libraryManager.GetItemsResult(query);
        var authorName = string.Empty;

        foreach (var item in queryResult.Items)
        {
            if (item is Book book)
            {
                // Try to extract author from path
                var pathParts = book.Path.Split('/');
                if (pathParts.Length >= 6)
                {
                    var authorFromPath = pathParts[5];
                    if (!string.IsNullOrEmpty(authorFromPath))
                    {
                        // Convert to "Last, First" format if possible
                        var authorParts = authorFromPath.Split(' ');
                        var formattedAuthorName = authorFromPath;
                        if (authorParts.Length > 1)
                        {
                            var lastName = authorParts[^1]; // Last element
                            var firstName = string.Join(" ", authorParts.Take(authorParts.Length - 1));
                            formattedAuthorName = $"{lastName}, {firstName}";
                        }

                        if (GetStableGuid(formattedAuthorName) == authorId)
                        {
                            authorName = formattedAuthorName;
                            feedDto.Entries.Add(CreateEntry(book, baseUrl));
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(authorName))
        {
            feedDto.Title = GetFeedName($"Books by {authorName}");
        }

        return feedDto;
    }

    /// <inheritdoc />
    public string? GetBookImage(Guid bookId)
    {
        var item = _libraryManager.GetItemById(bookId);
        return item?.PrimaryImagePath;
    }

    /// <inheritdoc />
    public string? GetBook(Guid bookId)
    {
        var item = _libraryManager.GetItemById(bookId);
        return item?.Path;
    }

    /// <inheritdoc />
    public FeedDto SearchBooks(string baseUrl, Guid userId, string searchTerm)
    {
        var searchResult = _searchEngine.GetSearchHints(new SearchQuery
        {
            Limit = 100,
            SearchTerm = searchTerm,
            IncludeItemTypes = BookItemTypes,
            UserId = userId
        });

        var entries = new List<EntryDto>(searchResult.Items.Count);
        foreach (var result in searchResult.Items)
        {
            if (result.Item is Book book)
            {
                entries.Add(CreateEntry(book, baseUrl));
            }
        }

        return new FeedDto
        {
            Id = Guid.NewGuid().ToString(),
            Links = new[]
            {
                new LinkDto("self", baseUrl + "/opds/search/" + searchTerm + "?", "application/atom+xml;profile=opds-catalog;kind=navigation"),
                new LinkDto("start", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;kind=navigation", "Start"),
                new LinkDto("up", baseUrl + "/opds", "application/atom+xml;profile=opds-catalog;type=feed;kind=navigation"),
                new LinkDto("search", baseUrl + "/opds/osd", "application/opensearchdescription+xml"),
                new LinkDto("search", baseUrl + "/opds/search/{searchTerms}", "application/atom+xml", "Search")
            },
            Title = GetFeedName(searchTerm),
            Author = PluginAuthor,
            Entries = entries
        };
    }

    /// <inheritdoc />
    public OpenSearchDescriptionDto GetSearchDescription(string baseUrl)
    {
        var dto = new OpenSearchDescriptionDto
        {
            Xmlns = "http://a9.com/-/spec/opensearch/1.1/",
            Description = "Jellyfin eBook Catalog",
            Developer = "Jellyfin",
            Contact = "https://github.com/jellyfin/jellyfin-plugin-opds",
            SyndicationRight = "open",
            Language = "en-EN",
            OutputEncoding = "UTF-8",
            InputEncoding = "UTF-8",
            ShortName = GetFeedName("Search"),
            LongName = GetFeedName("Search"),
            Url = new[]
            {
                new OpenSearchUrlDto
                {
                    Type = MediaTypeNames.Text.Html,
                    Template = baseUrl + "/opds/search/{searchTerms}"
                },
                new OpenSearchUrlDto
                {
                    Type = "application/atom+xml",
                    Template = baseUrl + "/opds/search?query={searchTerms}"
                }
            }
        };

        return dto;
    }

    private static Guid GetStableGuid(string input)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = SHA256.HashData(inputBytes);
        // Use first 16 bytes of the hash for the GUID
        byte[] guidBytes = new byte[16];
        Array.Copy(hashBytes, guidBytes, 16);
        return new Guid(guidBytes);
    }

    private string GetFeedName(string title)
    {
        var serverName = _serverApplicationHost.FriendlyName;
        return title + " - " + (string.IsNullOrEmpty(serverName) ? "Jellyfin" : serverName);
    }

    private EntryDto CreateEntry(Book book, string baseUrl)
    {
        var entry = new EntryDto(
            book.Name,
            book.Id.ToString(),
            book.DateModified)
        {
            Author = new AuthorDto
            {
                Name = book.GetParent().Name
            },
            Summary = book.Overview,
            Links = new List<LinkDto>()
        };

        if (!string.IsNullOrEmpty(book.PrimaryImagePath))
        {
            var imageMimeType = MimeTypes.GetMimeType(book.PrimaryImagePath);
            if (!string.IsNullOrEmpty(imageMimeType))
            {
                entry.Links.Add(new("http://opds-spec.org/image", baseUrl + "/opds/cover/" + book.Id, imageMimeType));
                entry.Links.Add(new("http://opds-spec.org/image/thumbnail", baseUrl + "/opds/cover/" + book.Id, imageMimeType));
            }
        }

        if (!string.IsNullOrEmpty(book.Path))
        {
            var bookMimeType = MimeTypes.GetMimeType(book.Path);
            if (!string.IsNullOrEmpty(bookMimeType))
            {
                entry.Links.Add(new("http://opds-spec.org/acquisition", baseUrl + "/opds/download/" + book.Id, bookMimeType)
                {
                    UpdateTime = book.DateModified,
                    Length = book.Size
                });
            }
        }

        return entry;
    }
}
