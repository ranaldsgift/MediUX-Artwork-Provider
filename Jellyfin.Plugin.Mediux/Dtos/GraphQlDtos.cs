using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Mediux.Dtos;

internal sealed class GraphQlResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<GraphQlError>? Errors { get; set; }
}

internal sealed class GraphQlError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class MovieSetsData
{
    [JsonPropertyName("movies_by_id")]
    public MovieItem? MoviesById { get; set; }
}

internal sealed class ShowSetsData
{
    [JsonPropertyName("shows_by_id")]
    public ShowItem? ShowsById { get; set; }
}

internal sealed class TvdbLookupData
{
    [JsonPropertyName("shows")]
    public List<IdOnlyItem>? Shows { get; set; }
}

internal sealed class MovieTvdbLookupData
{
    [JsonPropertyName("movies")]
    public List<IdOnlyItem>? Movies { get; set; }
}

internal sealed class IdOnlyItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("tvdb_id")]
    public string? TvdbId { get; set; }
}

internal sealed class MovieItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("movie_sets")]
    public List<MovieSetDto>? MovieSets { get; set; }
}

internal sealed class ShowItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("show_sets")]
    public List<ShowSetDto>? ShowSets { get; set; }
}

internal sealed class MovieSetDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("set_title")]
    public string? SetTitle { get; set; }

    [JsonPropertyName("user_created")]
    public UserCreatedDto? UserCreated { get; set; }

    [JsonPropertyName("date_updated")]
    public string? DateUpdated { get; set; }

    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }

    [JsonPropertyName("popularity_global")]
    public double? PopularityGlobal { get; set; }

    [JsonPropertyName("movie_poster")]
    public List<AssetDto>? MoviePoster { get; set; }

    [JsonPropertyName("movie_backdrop")]
    public List<AssetDto>? MovieBackdrop { get; set; }

    [JsonPropertyName("files")]
    public List<FileAssetDto>? Files { get; set; }
}

internal sealed class ShowSetDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("set_title")]
    public string? SetTitle { get; set; }

    [JsonPropertyName("user_created")]
    public UserCreatedDto? UserCreated { get; set; }

    [JsonPropertyName("date_updated")]
    public string? DateUpdated { get; set; }

    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }

    [JsonPropertyName("popularity_global")]
    public double? PopularityGlobal { get; set; }

    [JsonPropertyName("show_poster")]
    public List<AssetDto>? ShowPoster { get; set; }

    [JsonPropertyName("show_backdrop")]
    public List<AssetDto>? ShowBackdrop { get; set; }

    [JsonPropertyName("season_posters")]
    public List<SeasonPosterDto>? SeasonPosters { get; set; }

    [JsonPropertyName("titlecards")]
    public List<TitleCardDto>? Titlecards { get; set; }

    [JsonPropertyName("files")]
    public List<FileAssetDto>? Files { get; set; }
}

internal sealed class UserCreatedDto
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

internal sealed class AssetDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("modified_on")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("language")]
    public LanguageDto? Language { get; set; }
}

internal sealed class FileAssetDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("file_type")]
    public string? FileType { get; set; }

    [JsonPropertyName("modified_on")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("language")]
    public LanguageDto? Language { get; set; }
}

internal sealed class LanguageDto
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

internal sealed class SeasonPosterDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("modified_on")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("language")]
    public LanguageDto? Language { get; set; }

    [JsonPropertyName("season")]
    public SeasonRefDto? Season { get; set; }
}

internal sealed class SeasonRefDto
{
    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }
}

internal sealed class TitleCardDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("modified_on")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("language")]
    public LanguageDto? Language { get; set; }

    [JsonPropertyName("episode")]
    public EpisodeRefDto? Episode { get; set; }
}

internal sealed class EpisodeRefDto
{
    [JsonPropertyName("episode_number")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("season_id")]
    public SeasonRefDto? SeasonId { get; set; }
}
