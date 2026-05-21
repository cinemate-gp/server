using Cinemate.Core.Abstractions;
using Cinemate.Core.Contracts.Common;
using Cinemate.Core.Contracts.Movies;
using Cinemate.Core.Entities;
using Cinemate.Core.Entities.Auth;
using Cinemate.Core.Errors.Movie;
using Cinemate.Core.Repository_Contract;
using Cinemate.Core.Service_Contract;
using Cinemate.Repository.Abstractions;
using Cinemate.Repository.Data.Contexts;
using HtmlAgilityPack;
using Mapster;
using MapsterMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Cinemate.Repository.Errors.Authentication.AuthenticationError;

namespace Cinemate.Service.Services.Movies
{
	public class MovieService : IMovieService
	{
		private readonly IUnitOfWork _unitOfWork;
		private readonly IMapper _mapper;
		private readonly ApplicationDbContext _context;
		private readonly INotificationService _notificationService;
		private readonly UserManager<ApplicationUser> _userManager;
		private readonly HttpClient _httpClient;
		private readonly IHttpContextAccessor _httpContextAccessor;
		private readonly ILogger<MovieService> _logger;
		private static readonly HashSet<int> _returnedMovieIds = new();
		private const string TmdbApiKey = "196fb13a1f9bda525f29ed4e3543de8c";
		private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
		private const string OmdbApiKey = "226e9b2d";
		private const string OmdbBaseUrl = "https://www.omdbapi.com";
		private const string MovieRecommenderUrl = "http://cinemate-rs-api.ebe5hqc4gueefzed.italynorth.azurecontainer.io:5000/api/v1/user/recommend";
		private const string MovieSimilarityUrl = "http://cinemate-rs-api.ebe5hqc4gueefzed.italynorth.azurecontainer.io:5000/api/v1/movie/similar";

		public MovieService(IUnitOfWork unitOfWork,
			IMapper mapper,
			ApplicationDbContext context,
			IHttpContextAccessor httpContextAccessor,
			UserManager<ApplicationUser> userManager,
			INotificationService notificationService, ILogger<MovieService> logger = null)
		{
			_unitOfWork = unitOfWork;
			_mapper = mapper;
			_context = context;
			_httpClient = new HttpClient();
			_logger = logger;
			_httpContextAccessor = httpContextAccessor;
			_notificationService = notificationService;
			_userManager = userManager;
		}
		public async Task<IEnumerable<MoviesTopTenResponse>> GetMovieTopTenAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				var response = await _httpClient.GetAsync($"{TmdbBaseUrl}/trending/movie/day?api_key={TmdbApiKey}", cancellationToken);
				response.EnsureSuccessStatusCode();

				var content = await response.Content.ReadFromJsonAsync<TmdbTrendingResponse>(cancellationToken: cancellationToken);

				if (content?.Results == null || !content.Results.Any())
					return Enumerable.Empty<MoviesTopTenResponse>();

				var tmdbIds = content.Results.Select(m => m.Id).Take(10).ToList();

				var movies = await _context.Movies
					.Where(m => tmdbIds.Contains(m.TMDBId) && !m.IsDeleted && m.PosterPath != null && m.Trailer != null)
					.ToListAsync(cancellationToken);

				return _mapper.Map<IEnumerable<MoviesTopTenResponse>>(movies);
			}
			catch (Exception ex)
			{
				return Enumerable.Empty<MoviesTopTenResponse>();
			}
		}
		public async Task<IEnumerable<MoviesTopTenResponse>> GetMovieTopTenRatedAsync(CancellationToken cancellationToken = default)
		{
			var mostPopularityMovies = await _context.Movies
				.Where(m => m.IsDeleted != true && m.PosterPath != null && m.Trailer != null && m.ReleaseDate.Value.Year == DateTime.Now.Year)
				.OrderByDescending(m => m.Popularity)
				.ThenByDescending(m => m.ReleaseDate.Value.Year)
				.Take(10)
				.ToListAsync(cancellationToken);

			var orderBasedOnRating = mostPopularityMovies
				.OrderByDescending(m => ParseImdbRating(m.IMDBRating))
				.ToList();

			return _mapper.Map<IEnumerable<MoviesTopTenResponse>>(orderBasedOnRating);
		}
		public async Task<Result<MovieDetailsResponse>> GetMovieDetailsAsync(string? userId, int tmdbid, CancellationToken cancellationToken = default)
		{
			var movie = await _context.Movies
				.Include(m => m.CastMovies)
				.ThenInclude(cm => cm.Cast)
				.Include(m => m.MovieGenres)
				.ThenInclude(mg => mg.Genre)
				.Include(m => m.UserReviews)
				.FirstOrDefaultAsync(m => m.TMDBId == tmdbid, cancellationToken);

			if (movie is null || movie.IsDeleted == true)
				return Result.Failure<MovieDetailsResponse>(MovieErrors.MovieNotFound);

			var likedMovie = await _unitOfWork.Repository<UserLikeMovie>()
				.GetQueryable()
				.FirstOrDefaultAsync(w => w.UserId == userId && w.TMDBId == tmdbid, cancellationToken);

			var userStarMovie = await _unitOfWork.Repository<UserRateMovie>()
				.GetQueryable()
				.FirstOrDefaultAsync(w => w.UserId == userId && w.TMDBId == tmdbid, cancellationToken);

			var watchedMovie = await _unitOfWork.Repository<UserWatchedMovie>()
				.GetQueryable()
				.FirstOrDefaultAsync(w => w.UserId == userId && w.TMDBId == tmdbid, cancellationToken);

			var watcheListMovie = await _unitOfWork.Repository<UserMovieWatchList>()
				.GetQueryable()
				.FirstOrDefaultAsync(w => w.UserId == userId && w.TMDBId == tmdbid, cancellationToken);

			var reviews = await _context.UserReviewMovies
				.Include(r => r.User)
				.ThenInclude(x => x.RatedMovies)
				.Where(r => r.TMDBId == tmdbid)
				.Select(r => new MovieReviewResponse(
					r.UserId,
					r.User.UserName!,
					r.TMDBId,
					r.User.FullName,
					r.User.ProfilePic,
					r.ReviewId,
					r.ReviewBody,
					r.ReviewType,
					r.ReviewConfidence,
					r.ReviewedOn,
					r.User.RatedMovies.Any(rm => rm.TMDBId == tmdbid) ? r.User.RatedMovies.First(rm => rm.TMDBId == tmdbid).Stars : 0
				))
				.ToListAsync(cancellationToken);
			int totalReviews = reviews.Count;
			string movieReviewType = "Mixed";
			if (totalReviews > 0)
			{
				int positiveCount = reviews.Count(r => r.ReviewType == "positive" || r.ReviewType == "very positive");
				int negativeCount = reviews.Count(r => r.ReviewType == "negative" || r.ReviewType == "very negative");

				double positivePercentage = (double)positiveCount / totalReviews;
				double negativePercentage = (double)negativeCount / totalReviews;

				if (positivePercentage >= 0.75)
					movieReviewType = "Very Positive";
				else if (positivePercentage > 0.5)
					movieReviewType = "Positive";
				else if (negativePercentage >= 0.75)
					movieReviewType = "Very Negative";
				else if (negativePercentage > 0.5)
					movieReviewType = "Negative";
			}

			var response = movie.Adapt<MovieDetailsResponse>();
			response = response with
			{
				Stars = userStarMovie?.Stars,
				IsLiked = likedMovie != null,
				IsInWatchList = watcheListMovie != null,
				IsWatched = watchedMovie != null,
				MovieReviews = reviews,
				MovieReviewType = movieReviewType
			};
			return Result.Success(response);
		}
		public async Task<IEnumerable<MovieDetailsRandomResponse>> GetMovieRandomAsync(CancellationToken cancellationToken = default)
		{
			var top100Movies = _context.Movies
				.Where(m => m.Popularity != null && m.IsDeleted != true && m.PosterPath != null && m.Trailer != null && m.ReleaseDate.Value.Year > 1990)
				.ToList()
				.OrderByDescending(m => m.Popularity)
				.OrderByDescending(m => m.ReleaseDate.Value.Year)
				.Take(100)
				.ToList();

			var availableMovies = top100Movies.Where(m => !_returnedMovieIds.Contains(m.TMDBId)).ToList();
			var movieIds = availableMovies
				.OrderBy(_ => Guid.NewGuid())
				.Take(10)
				.Select(m => m.TMDBId)
				.ToList();

			foreach (var id in movieIds)
				_returnedMovieIds.Add(id);

			var randomMovies = await _context.Movies
				.Include(m => m.MovieGenres)
				.ThenInclude(mg => mg.Genre)
				.Where(m => movieIds.Contains(m.TMDBId))
				.ToListAsync(cancellationToken);

			return randomMovies.Select(m => m.Adapt<MovieDetailsRandomResponse>()).ToList();
		}
		public async Task<IEnumerable<MovieTrendingResponse>> GetTrendingMoviesAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				var response = await _httpClient.GetAsync($"{TmdbBaseUrl}/trending/movie/week?api_key={TmdbApiKey}", cancellationToken);
				response.EnsureSuccessStatusCode();

				var content = await response.Content.ReadFromJsonAsync<TmdbTrendingResponse>(cancellationToken: cancellationToken);

				if (content?.Results == null || !content.Results.Any())
					return Enumerable.Empty<MovieTrendingResponse>();

				var tmdbIds = content.Results.Select(m => m.Id).Take(10).ToList();

				var movies = await _context.Movies
					.Include(m => m.MovieGenres)
					.ThenInclude(mg => mg.Genre)
					.Where(m => tmdbIds.Contains(m.TMDBId) && !m.IsDeleted)
					.ToListAsync(cancellationToken);

				return movies.Select(m => m.Adapt<MovieTrendingResponse>()).ToList();
			}
			catch (Exception ex)
			{
				return Enumerable.Empty<MovieTrendingResponse>();
			}
		}
        public async Task<Result<IEnumerable<MovieRecommendationResponse>>> GetRecommendedMoviesAsync(string userName, CancellationToken cancellationToken)
        {
            var user = await _userManager.FindByNameAsync(userName);
            if (user is null)
                return Result.Failure<IEnumerable<MovieRecommendationResponse>>(UserErrors.Unauthorized);

            var request = new MovieRecommendationRequest
            (
                user.Id,
                DateTime.UtcNow.Year - user.BirthDay.Year
            );

            var recommendationResult = await GetMovieRecommendationsAsync(request, cancellationToken);

            if (recommendationResult == null)
                return Result.Failure<IEnumerable<MovieRecommendationResponse>>(MovieErrors.RecommandationNotFound);

            // Calculate the current year for normalizing release years
            int currentYear = DateTime.UtcNow.Year;

            // Sort recommendations prioritizing newer movies while still respecting ML model scores
            var sortedRecommendations = recommendationResult
                .OrderByDescending(m => {
                    // Calculate a normalized release year score (0 to 1)
                    double yearScore = 0;
                    if (m.ReleaseYear.HasValue)
                    {
                        // Normalize release year to a 0-1 scale, with newer movies closer to 1
                        yearScore = Math.Min(1.0, Math.Max(0, (double)(m.ReleaseYear.Value - (currentYear - 10)) / 10));
                    }

                    // Calculate combined score (70% ML recommendation, 30% release year recency)
                    return (m.Score * 0.7) + (yearScore * 0.3);
                })
                .ToList();

            return Result.Success<IEnumerable<MovieRecommendationResponse>>(sortedRecommendations);
        }
        public async Task<Result<IEnumerable<MoviesTopTenResponse>>> GetMovieSimilarityAsync(int tmdbId, CancellationToken cancellationToken)
		{
			var similarityResult = await GetMovieSimilarityFromApiAsync(tmdbId, cancellationToken);

			if (similarityResult == null || !similarityResult.Any())
				return Result.Success(Enumerable.Empty<MoviesTopTenResponse>());

			var tmdbIds = similarityResult
				.Where(s => s != null && s.Recommendations != null)
				.SelectMany(s => s.Recommendations)
				.Where(id => id != 0)
				.ToList();

			if (!tmdbIds.Any())
				return Result.Success(Enumerable.Empty<MoviesTopTenResponse>());

			var movies = await _context.Movies
				.Where(m => tmdbIds.Contains(m.TMDBId) && !m.IsDeleted && m.PosterPath != null)
				.ToListAsync(cancellationToken);

			var result = movies.Select(m => new MoviesTopTenResponse(
				m.TMDBId,
				m.Title,
				m.PosterPath,
				m.IMDBRating,
				m.MPA
			)).ToList();

			return Result.Success<IEnumerable<MoviesTopTenResponse>>(result);
		}
		public async Task<IEnumerable<MoviesTopTenResponse>> GetMovieBasedOnGeneraAsync(MovieGeneraRequest? request, CancellationToken cancellationToken = default)
		{
			var today = DateOnly.FromDateTime(DateTime.Today);
			IQueryable<Movie> query;
			if (request == null || string.IsNullOrWhiteSpace(request.Genere))
				query = _context.Movies
					.Where(m => m.ReleaseDate != null && m.ReleaseDate <= today && !m.IsDeleted);
			else
			{
				var genreName = request.Genere.Trim();
				query = _context.Movies
					.Where(m => m.MovieGenres.Any(mg => mg.Genre.Name == genreName) &&
						  m.ReleaseDate != null &&
						  m.ReleaseDate <= today &&
						  !m.IsDeleted);
			}

			var movies = await query
				.OrderByDescending(m => m.ReleaseDate)
				.Select(m => new MoviesTopTenResponse(
					m.TMDBId,
					m.Title,
					m.PosterPath,
					m.IMDBRating,
					m.MPA
				))
				.AsNoTracking()
				.Take(100)
				.ToListAsync(cancellationToken);

			var randomMovies = movies
				.OrderBy(_ => Guid.NewGuid())
				.Take(10)
				.ToList();

			return randomMovies;
		}
		public async Task<PaginatedList<MoviesTopTenResponse>> GetPaginatedMovieBasedAsync(RequestFilters request, CancellationToken cancellationToken = default)
		{
			var query = _context.Movies
				.Where(m => !m.IsDeleted && m.PosterPath != null && m.Trailer != null)
				.AsQueryable();
			if (!string.IsNullOrEmpty(request.SearchValue))
				query = query.Where(m => m.Title.Contains(request.SearchValue));
			if (!string.IsNullOrEmpty(request.MPA))
				query = query.Where(m => m.MPA == request.MPA);
			if (!string.IsNullOrEmpty(request.Gener))
				query = query.Where(m => m.MovieGenres.Any(mg => mg.Genre.Name == request.Gener));
			if (!string.IsNullOrEmpty(request.Year))
				query = query.Where(m => m.ReleaseDate.HasValue && m.ReleaseDate.Value.Year.ToString().Contains(request.Year));
			query = request.SortDirection == SortDirection.Descending
				? query.OrderByDescending(m => m.Popularity).ThenByDescending(m => m.ReleaseDate.Value.Year)
				: query.OrderBy(m => m.Popularity).ThenByDescending(m => m.ReleaseDate.Value.Year);
			var movies = query
				.Select(m => new MoviesTopTenResponse(
					m.TMDBId,
					m.Title,
					m.PosterPath,
					m.IMDBRating,
					m.MPA
				))
				.AsNoTracking();
			var response = await PaginatedList<MoviesTopTenResponse>.CreateAsync(
				movies,
				request.PageNumber,
				request.PageSize,
				cancellationToken
			);
			return response;
		}
		public async Task<Result<IEnumerable<SearchResponse>>> GetSearchForMovieActorUsersAsync(RequestSearch request, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(request.SearchValue))
				return Result.Success<IEnumerable<SearchResponse>>(new List<SearchResponse>());

			var searchTerm = request.SearchValue.Trim().ToLower();
			var normalizedSearchTerm = searchTerm.Replace(" ", "");

			var movieResults = await _context.Movies
				.Where(m => m.Title != null && m.Title.ToLower().Contains(searchTerm) && (m.PosterPath != null || m.Trailer != null) && !m.IsDeleted)
				.Take(50)
				.OrderByDescending(m => m.Popularity)
				.Select(m => new SearchResponse(
					m.TMDBId.ToString(),
					m.Title,
					m.PosterPath ?? string.Empty,
					"Movie"
				))
				.AsNoTracking()

				.ToListAsync(cancellationToken);

			var actorResults = await _context.Casts
				.Where(c => c.Name != null && c.Name.ToLower().Contains(searchTerm))
				.Take(50)
				.Select(c => new SearchResponse(
					c.CastId.ToString(),
					c.Name,
					c.ProfilePath ?? string.Empty,
					"Actor"
				))
				.AsNoTracking()
				.ToListAsync(cancellationToken);

			var userResults = await _context.Users
				.Where(u => !u.IsDisabled &&
					(u.FullName.ToLower().Contains(searchTerm) ||
					 u.UserName.ToLower().Contains(searchTerm) ||
					 u.FullName.ToLower().Replace(" ", "").Contains(normalizedSearchTerm) ||
					 u.UserName.ToLower().Replace(" ", "").Contains(normalizedSearchTerm)))
				.Take(50)
				.Select(u => new SearchResponse(
					u.UserName,
					u.FullName,
					u.ProfilePic ?? string.Empty,
					"User"
				))
				.AsNoTracking()
				.ToListAsync(cancellationToken);

			var combinedResults = movieResults
				.Concat(actorResults)
				.Concat(userResults)
				.ToList();

			return Result.Success<IEnumerable<SearchResponse>>(combinedResults);
		}
		public async Task<Result> UpCommingMovieAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				var today = DateOnly.FromDateTime(DateTime.Today);
				var newlyReleasedMovie = await _context.Movies
					.Where(m => m.ReleaseDate.HasValue &&
							   m.ReleaseDate == today &&
							   !m.IsDeleted &&
							   m.PosterPath != null)
					.OrderByDescending(m => m.Popularity)
					.FirstOrDefaultAsync(cancellationToken);

				var existingNotification = await _unitOfWork.Repository<Notification>()
					.GetQueryable()
					.FirstOrDefaultAsync(n => n.ActionId == newlyReleasedMovie.TMDBId.ToString() && n.NotificationType == "NewRelease" && n.CreatedAt.Date == DateTime.UtcNow.Date, cancellationToken);

				if (existingNotification != null)
				{
					_logger?.LogInformation($"Notification for movie '{newlyReleasedMovie.Title}' already sent today");
					return Result.Success();
				}
				var allUsers = await _userManager.Users
					.Where(u => !u.IsDisabled && u.IsEnableNotificationNewRelease)
					.ToListAsync(cancellationToken);

				var notifications = new List<Notification>();
				foreach (var user in allUsers)
				{
					var notification = new Notification
					{
						UserId = user.Id,
						Message = $"🎬 New movie released today: {newlyReleasedMovie.Title}!",
						ActionId = newlyReleasedMovie.TMDBId.ToString(),
						NotificationType = "NewRelease",
						CreatedAt = DateTime.UtcNow
					};
					notifications.Add(notification);
				}
				if (notifications.Any())
				{
					await _unitOfWork.Repository<Notification>().AddRangeAsync(notifications);
					await _unitOfWork.CompleteAsync();

					_logger?.LogInformation($"Created {notifications.Count} new release notifications for '{newlyReleasedMovie.Title}'");
					foreach (var notification in notifications)
					{
						var user = allUsers.FirstOrDefault(u => u.Id == notification.UserId);
						if (user != null)
							await _notificationService.SendRealTimeNotificationAsync(notification, user, cancellationToken);
					}
				}
				return Result.Success();
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error creating new release movie notifications");
				return Result.Failure(new Error("NewReleaseError", "Failed to create new release movie notifications", 500));
			}
		}
		public async Task<int> UpdateMovieRatingsAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				var today = DateOnly.FromDateTime(DateTime.Today);
				var movies = await _context.Movies
					.Where(m => m.IMDBId > 0 && !m.IsDeleted && m.ReleaseDate.HasValue && m.ReleaseDate <= today)
					.OrderByDescending(m => m.ReleaseDate)
					//.Skip(600)
					.Take(600)
					.ToListAsync(cancellationToken);

				int updatedMoviesCount = 0;

				foreach (var movie in movies)
				{
					try
					{
						// Fetch latest movie details from TMDB
						var tmdbResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{movie.TMDBId}?api_key={TmdbApiKey}", cancellationToken);
						if (!tmdbResponse.IsSuccessStatusCode)
						{
							_logger?.LogWarning($"Failed to fetch TMDB details for movie {movie.TMDBId}");
							continue;
						}
						var tmdbData = await tmdbResponse.Content.ReadFromJsonAsync<TmdbUpcomingMovie>(cancellationToken: cancellationToken);

						// Update logo if null
						if (string.IsNullOrEmpty(movie.LogoPath))
						{
							var imagesResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{movie.TMDBId}/images?api_key={TmdbApiKey}", cancellationToken);
							if (imagesResponse.IsSuccessStatusCode)
							{
								var images = await imagesResponse.Content.ReadFromJsonAsync<TmdbImagesResponse>(cancellationToken: cancellationToken);
								var englishLogo = images?.Logos?.FirstOrDefault(l => l.Iso_639_1 == "en");
								if (englishLogo != null)
								{
									movie.LogoPath = englishLogo.FilePath;
								}
							}
						}

						// Update other details if changed
						if (tmdbData != null)
						{
							if (movie.Popularity != tmdbData.Popularity)
								movie.Popularity = tmdbData.Popularity;
							if (movie.Tagline != tmdbData.Tagline)
								movie.Tagline = tmdbData.Tagline;
							if (movie.Status != tmdbData.Status)
								movie.Status = tmdbData.Status;
							if (movie.Revenue != tmdbData.Revenue)
								movie.Revenue = tmdbData.Revenue;
							if (movie.Budget != tmdbData.Budget)
								movie.Budget = tmdbData.Budget;
							if (movie.BackdropPath != tmdbData.BackdropPath)
								movie.BackdropPath = tmdbData.BackdropPath;
							if (movie.PosterPath != tmdbData.PosterPath)
								movie.PosterPath = tmdbData.PosterPath;

							// Fetch and update trailer if changed
							var videosResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{movie.TMDBId}/videos?api_key={TmdbApiKey}", cancellationToken);
							if (videosResponse.IsSuccessStatusCode)
							{
								var videos = await videosResponse.Content.ReadFromJsonAsync<TmdbVideosResponse>(cancellationToken: cancellationToken);
								var officialTrailer = videos?.Results?.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
									v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase) &&
									v.Official);
								if (officialTrailer != null && movie.Trailer != officialTrailer.Key)
								{
									movie.Trailer = officialTrailer.Key;
								}
							}
						}

						// Existing rating update logic
						string imdbId = $"tt{movie.IMDBId.ToString().PadLeft(7, '0')}";
						string imdbRating = null;
						string mpa = null;
						string rottenTomatoesRating = null;
						string metacriticRating = null;

						var omdbResponse = await _httpClient.GetAsync($"{OmdbBaseUrl}/?i={imdbId}&apikey={OmdbApiKey}", cancellationToken);
						if (omdbResponse.IsSuccessStatusCode)
						{
							var omdbData = await omdbResponse.Content.ReadFromJsonAsync<OmdbResponse>(cancellationToken: cancellationToken);
							if (omdbData != null)
							{
								imdbRating = !string.IsNullOrEmpty(omdbData.ImdbRating) && omdbData.ImdbRating != "N/A"
									? omdbData.ImdbRating + "/10"
									: null;
								mpa = !string.IsNullOrEmpty(omdbData.Rated) && omdbData.Rated != "N/A"
									? omdbData.Rated
									: null;

								if (omdbData.Ratings != null)
								{
									var rtRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Rotten Tomatoes");
									rottenTomatoesRating = rtRating?.Value;

									var mcRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Metacritic");
									metacriticRating = mcRating?.Value;
								}
							}
						}

						if (string.IsNullOrEmpty(imdbRating))
						{
							try
							{
								var imdbUrl = $"https://www.imdb.com/title/{imdbId}/";
								var html = await _httpClient.GetStringAsync(imdbUrl);
								var doc = new HtmlDocument();
								doc.LoadHtml(html);

								var ratingNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'sc-d541859f-1') and contains(@class, 'imUuxf')]");
								if (ratingNode != null && double.TryParse(ratingNode.InnerText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var scrapedRating))
								{
									imdbRating = $"{scrapedRating:F1}/10";
									_logger?.LogInformation($"Scraped IMDb rating {imdbRating} for movie {movie.TMDBId} - {movie.Title}");
								}
								else
								{
									_logger?.LogWarning($"Failed to scrape IMDb rating for movie {movie.TMDBId} - {movie.Title}");
								}

								await Task.Delay(1000, cancellationToken);
							}
							catch (Exception ex)
							{
								_logger?.LogWarning(ex, $"Error scraping IMDb rating for movie {movie.TMDBId} - {movie.Title}");
							}
						}

						imdbRating = imdbRating ?? "0.0/10";
						if (imdbRating == "0.0/10")
						{
							_logger?.LogInformation($"Set default IMDb rating 0.0/10 for movie {movie.TMDBId} - {movie.Title}");
						}

						// Check if any rating or MPA has changed
						bool hasChanged = movie.IMDBRating != imdbRating ||
										movie.RottenTomatoesRating != rottenTomatoesRating ||
										movie.MetacriticRating != metacriticRating ||
										movie.MPA != mpa;

						if (hasChanged)
						{
							movie.IMDBRating = imdbRating;
							movie.RottenTomatoesRating = rottenTomatoesRating ?? movie.RottenTomatoesRating;
							movie.MetacriticRating = metacriticRating ?? movie.MetacriticRating;
							movie.MPA = mpa ?? movie.MPA;
						}

						updatedMoviesCount++;
					}
					catch (Exception ex)
					{
						_logger?.LogWarning(ex, $"Error processing movie {movie.TMDBId}");
						continue;
					}
				}

				await _context.SaveChangesAsync(cancellationToken);
				_logger?.LogInformation($"Updated {updatedMoviesCount} movies");
				return updatedMoviesCount;
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error updating movie ratings");
				return 0;
			}
		}
		public async Task<int> FetchAndSaveUpcomingMoviesAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				int addedMoviesCount = 0;
				int currentPage = 1;
				int totalPages = 1;
				var startDate = "2025-06-20";
				var endDate = "2025-06-21";

				var castCache = new Dictionary<int, Cast>();
				var genreCache = new Dictionary<int, Genre>();
				var castMovieCache = new HashSet<(int CastId, int TmdbId)>();

				while (currentPage <= totalPages)
				{
					var response = await _httpClient.GetAsync(
						$"{TmdbBaseUrl}/discover/movie?api_key={TmdbApiKey}&primary_release_date.gte={startDate}&primary_release_date.lte={endDate}&sort_by=primary_release_date.desc&page={currentPage}",
						cancellationToken);
					response.EnsureSuccessStatusCode();

					var content = await response.Content.ReadFromJsonAsync<TmdbUpcomingResponse>(cancellationToken: cancellationToken);

					if (content?.Results == null || !content.Results.Any())
					{
						_logger?.LogInformation($"No movies found on page {currentPage}.");
						break;
					}

					if (currentPage == 1)
					{
						totalPages = content.TotalPages;
						_logger?.LogInformation($"Total pages to fetch: {totalPages}");
					}

					foreach (var upcomingMovie in content.Results)
					{
						var existingMovie = await _context.Movies
							.AsNoTracking()
							.FirstOrDefaultAsync(m => m.TMDBId == upcomingMovie.Id, cancellationToken);

						if (existingMovie != null)
						{
							_logger?.LogInformation($"Found existing movie {upcomingMovie.Id} - {upcomingMovie.Title}. Skipping.");
							continue;
						}

						var movieDetailsResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{upcomingMovie.Id}?api_key={TmdbApiKey}", cancellationToken);
						if (!movieDetailsResponse.IsSuccessStatusCode)
						{
							_logger?.LogWarning($"Failed to fetch details for movie {upcomingMovie.Id} - {upcomingMovie.Title}.");
							continue;
						}

						var movieDetails = await movieDetailsResponse.Content.ReadFromJsonAsync<TmdbUpcomingMovie>(cancellationToken: cancellationToken);
						if (movieDetails == null)
						{
							_logger?.LogWarning($"No movie details returned for TMDB ID {upcomingMovie.Id}.");
							continue;
						}

						string trailerUrl = null;
						var videosResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{upcomingMovie.Id}/videos?api_key={TmdbApiKey}", cancellationToken);
						if (videosResponse.IsSuccessStatusCode)
						{
							var videos = await videosResponse.Content.ReadFromJsonAsync<TmdbVideosResponse>(cancellationToken: cancellationToken);
							if (videos?.Results != null && videos.Results.Any())
							{
								var officialTrailer = videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
									v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase) &&
									v.Official) ??
									videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
									v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase)) ??
									videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
									v.Type.Equals("Teaser", StringComparison.OrdinalIgnoreCase)) ??
									videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase));

								if (officialTrailer != null)
									trailerUrl = officialTrailer.Key;
							}
						}

						var creditsResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{upcomingMovie.Id}/credits?api_key={TmdbApiKey}", cancellationToken);
						var credits = creditsResponse.IsSuccessStatusCode
							? await creditsResponse.Content.ReadFromJsonAsync<TmdbCreditsResponse>(cancellationToken: cancellationToken)
							: null;

						string logoPath = null;
						var imagesResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{upcomingMovie.Id}/images?api_key={TmdbApiKey}", cancellationToken);
						if (imagesResponse.IsSuccessStatusCode)
						{
							var images = await imagesResponse.Content.ReadFromJsonAsync<TmdbImagesResponse>(cancellationToken: cancellationToken);
							var englishLogo = images?.Logos?.FirstOrDefault(l => l.Iso_639_1 == "en");
							logoPath = englishLogo?.FilePath;
						}

						string imdbId = movieDetails.ImdbId;
						string imdbRating = null;
						string rottenTomatoesRating = null;
						string metacriticRating = null;
						string mpa = null;

						if (!string.IsNullOrEmpty(imdbId))
						{
							var omdbResponse = await _httpClient.GetAsync($"{OmdbBaseUrl}/?i={imdbId}&apikey={OmdbApiKey}", cancellationToken);
							if (omdbResponse.IsSuccessStatusCode)
							{
								var omdbData = await omdbResponse.Content.ReadFromJsonAsync<OmdbResponse>(cancellationToken: cancellationToken);
								if (omdbData != null)
								{
									imdbRating = !string.IsNullOrEmpty(omdbData.ImdbRating) && omdbData.ImdbRating != "N/A"
										? omdbData.ImdbRating + "/10"
										: null;
									if (!string.IsNullOrEmpty(omdbData.Rated) && MpaMapping.TryGetValue(omdbData.Rated, out string mappedMpa))
										mpa = mappedMpa;
									else
										mpa = "Unrated";

									if (omdbData.Ratings != null)
									{
										var rtRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Rotten Tomatoes");
										rottenTomatoesRating = rtRating?.Value;

										var mcRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Metacritic");
										metacriticRating = mcRating?.Value;
									}
								}
							}
						}
						var movie = new Movie
						{
							TMDBId = upcomingMovie.Id,
							IMDBId = !string.IsNullOrEmpty(imdbId) ? int.Parse(imdbId.Replace("tt", "")) : 0,
							Title = upcomingMovie.Title,
							Overview = upcomingMovie.Overview,
							ReleaseDate = !string.IsNullOrEmpty(upcomingMovie.ReleaseDate) ? DateOnly.Parse(upcomingMovie.ReleaseDate) : null,
							Runtime = movieDetails.Runtime,
							Language = upcomingMovie.OriginalLanguage,
							PosterPath = upcomingMovie.PosterPath,
							BackdropPath = upcomingMovie.BackdropPath,
							Trailer = trailerUrl,
							Budget = movieDetails.Budget,
							Revenue = movieDetails.Revenue,
							Status = movieDetails.Status,
							Tagline = movieDetails.Tagline,
							IMDBRating = imdbRating,
							RottenTomatoesRating = rottenTomatoesRating,
							MetacriticRating = metacriticRating,
							MPA = mpa,
							Popularity = upcomingMovie.Popularity,
							LogoPath = logoPath,
							IsDeleted = false
						};
						await _context.Movies.AddAsync(movie, cancellationToken);

						if (movieDetails.Genres != null)
						{
							foreach (var genre in movieDetails.Genres)
							{
								Genre existingGenre;
								if (!genreCache.TryGetValue(genre.Id, out existingGenre))
								{
									existingGenre = await _context.Genres
										.FirstOrDefaultAsync(g => g.Id == genre.Id, cancellationToken);

									if (existingGenre == null)
									{
										existingGenre = new Genre
										{
											Id = genre.Id,
											Name = genre.Name
										};
										await _context.Genres.AddAsync(existingGenre, cancellationToken);
									}
									else
									{
										_context.Genres.Attach(existingGenre);
									}
									genreCache[genre.Id] = existingGenre;
								}

								var movieGenre = new MovieGenre
								{
									TMDBId = movie.TMDBId,
									GenreId = genre.Id,
									Movie = movie,
									Genre = existingGenre
								};
								await _context.MovieGenres.AddAsync(movieGenre, cancellationToken);
							}
						}

						if (credits != null)
						{
							var peopleToAdd = new List<(int Id, string Name, string Role, string Department, string ProfilePath, int? Gender, double? Popularity, bool IsCast, string Character)>();

							if (credits.Cast != null)
							{
								foreach (var castMember in credits.Cast)
								{
									if (!peopleToAdd.Any(p => p.Id == castMember.Id))
									{
										peopleToAdd.Add((castMember.Id, castMember.Name, castMember.Character, castMember.KnownForDepartment,
											castMember.ProfilePath, castMember.Gender, castMember.Popularity, true, castMember.Character));
									}
								}
							}

							if (credits.Crew != null)
							{
								var importantJobs = new HashSet<string> { "Director", "Writer", "Editor", "Producer" };
								foreach (var crewMember in credits.Crew.Where(c => importantJobs.Contains(c.Job)))
								{
									if (!peopleToAdd.Any(p => p.Id == crewMember.Id))
									{
										peopleToAdd.Add((crewMember.Id, crewMember.Name, crewMember.Job, crewMember.Department,
											crewMember.ProfilePath, crewMember.Gender, crewMember.Popularity, false, crewMember.Department));
									}
								}
							}

							var topPeople = peopleToAdd
								.OrderByDescending(p => p.Popularity)
								.Take(25)
								.ToList();

							foreach (var person in topPeople)
							{
								var castMovieKey = (person.Id, movie.TMDBId);
								if (castMovieCache.Contains(castMovieKey))
								{
									_logger?.LogInformation($"Skipping duplicate CastMovie for CastId {person.Id} and TmdbId {movie.TMDBId}");
									continue;
								}

								var existingCastMovie = await _context.CastMovie
									.AsNoTracking()
									.FirstOrDefaultAsync(cm => cm.CastId == person.Id && cm.TmdbId == movie.TMDBId, cancellationToken);

								if (existingCastMovie != null)
								{
									_logger?.LogInformation($"Found existing CastMovie for CastId {person.Id} and TmdbId {movie.TMDBId}. Skipping.");
									castMovieCache.Add(castMovieKey);
									continue;
								}

								Cast existingCast;
								if (!castCache.TryGetValue(person.Id, out existingCast))
								{
									existingCast = await _context.Casts
										.AsNoTracking()
										.FirstOrDefaultAsync(c => c.CastId == person.Id, cancellationToken);

									if (existingCast == null)
									{
										var castDetailsResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/person/{person.Id}?api_key={TmdbApiKey}", cancellationToken);
										var castDetails = castDetailsResponse.IsSuccessStatusCode
											? await castDetailsResponse.Content.ReadFromJsonAsync<TmdbPersonResponse>(cancellationToken: cancellationToken)
											: null;

										existingCast = new Cast
										{
											CastId = person.Id,
											Name = person.Name,
											ProfilePath = person.ProfilePath,
											Gender = person.Gender,
											Popularity = person.Popularity,
											KnownForDepartment = person.Department,
											Biography = castDetails?.Biography,
											BirthDay = !string.IsNullOrEmpty(castDetails?.Birthday) ? DateOnly.Parse(castDetails.Birthday) : null,
											DeathDay = !string.IsNullOrEmpty(castDetails?.Deathday) ? DateOnly.Parse(castDetails.Deathday) : null,
											PlaceOfBirth = castDetails?.PlaceOfBirth
										};
										await _context.Casts.AddAsync(existingCast, cancellationToken);
									}
									else
									{
										_context.Casts.Attach(existingCast);
									}
									castCache[person.Id] = existingCast;
								}

								var castMovie = new CastMovie
								{
									CastId = person.Id,
									TmdbId = movie.TMDBId,
									Role = person.IsCast ? "cast" : "crew",
									Extra = person.IsCast ? person.Character : person.Department,
									Movie = movie,
									Cast = existingCast
								};
								await _context.CastMovie.AddAsync(castMovie, cancellationToken);
								castMovieCache.Add(castMovieKey);
							}
						}

						await _context.SaveChangesAsync(cancellationToken);
						castCache.Clear();
						genreCache.Clear();
						castMovieCache.Clear();
						_context.ChangeTracker.Clear();
						addedMoviesCount++;
						_logger?.LogInformation($"Added movie {upcomingMovie.Id} - {upcomingMovie.Title}");
					}

					_logger?.LogInformation($"Processed page {currentPage} of {totalPages}. Added {addedMoviesCount} movies so far.");
					currentPage++;
				}

				_logger?.LogInformation($"Added {addedMoviesCount} new upcoming movies to database");
				return addedMoviesCount;
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error fetching and saving upcoming movies");
				return 0;
			}
		}
		public async Task<int> FetchAndSaveMovieByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken = default)
		{
			try
			{
				_context.ChangeTracker.Clear();
				var existingMovie = await _context.Movies
					.AsNoTracking()
					.FirstOrDefaultAsync(m => m.TMDBId == tmdbId, cancellationToken);

				if (existingMovie != null)
				{
					_logger?.LogInformation($"Movie with TMDB ID {tmdbId} already exists in database.");
					return 0;
				}
				var movieDetailsResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{tmdbId}?api_key={TmdbApiKey}", cancellationToken);
				if (!movieDetailsResponse.IsSuccessStatusCode)
				{
					_logger?.LogWarning($"Failed to fetch movie details for TMDB ID {tmdbId}. Status: {movieDetailsResponse.StatusCode}");
					return 0;
				}

				var movieDetails = await movieDetailsResponse.Content.ReadFromJsonAsync<TmdbUpcomingMovie>(cancellationToken: cancellationToken);
				if (movieDetails == null)
				{
					_logger?.LogWarning($"No movie details returned for TMDB ID {tmdbId}.");
					return 0;
				}
				string trailerUrl = null;
				var videosResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{tmdbId}/videos?api_key={TmdbApiKey}", cancellationToken);
				if (videosResponse.IsSuccessStatusCode)
				{
					var videos = await videosResponse.Content.ReadFromJsonAsync<TmdbVideosResponse>(cancellationToken: cancellationToken);
					if (videos?.Results != null && videos.Results.Any())
					{
						var officialTrailer = videos.Results.FirstOrDefault(v =>
							v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
							v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase) &&
							v.Official) ??
							videos.Results.FirstOrDefault(v =>
								v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
								v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase)) ??
							videos.Results.FirstOrDefault(v =>
								v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
								v.Type.Equals("Teaser", StringComparison.OrdinalIgnoreCase)) ??
							videos.Results.FirstOrDefault(v =>
								v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase));

						trailerUrl = officialTrailer?.Key;
					}
				}
				if (string.IsNullOrEmpty(trailerUrl))
					_logger?.LogInformation($"No trailer available for movie with TMDB ID {tmdbId}. Proceeding to save movie.");

				// Fetch credits
				var creditsResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{tmdbId}/credits?api_key={TmdbApiKey}", cancellationToken);
				var credits = creditsResponse.IsSuccessStatusCode
					? await creditsResponse.Content.ReadFromJsonAsync<TmdbCreditsResponse>(cancellationToken: cancellationToken)
					: null;

				string logoPath = null;
				var imagesResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/movie/{tmdbId}/images?api_key={TmdbApiKey}", cancellationToken);
				if (imagesResponse.IsSuccessStatusCode)
				{
					var images = await imagesResponse.Content.ReadFromJsonAsync<TmdbImagesResponse>(cancellationToken: cancellationToken);
					var englishLogo = images?.Logos?.FirstOrDefault(l => l.Iso_639_1 == "en");
					logoPath = englishLogo?.FilePath;
				}
				string imdbId = movieDetails.ImdbId;
				string imdbRating = null;
				string rottenTomatoesRating = null;
				string metacriticRating = null;
				string mpa = null;

				if (!string.IsNullOrEmpty(imdbId))
				{
					var omdbResponse = await _httpClient.GetAsync($"{OmdbBaseUrl}/?i={imdbId}&apikey={OmdbApiKey}", cancellationToken);
					if (omdbResponse.IsSuccessStatusCode)
					{
						var omdbData = await omdbResponse.Content.ReadFromJsonAsync<OmdbResponse>(cancellationToken: cancellationToken);
						if (omdbData != null)
						{
							imdbRating = !string.IsNullOrEmpty(omdbData.ImdbRating) && omdbData.ImdbRating != "N/A"
								? omdbData.ImdbRating + "/10"
								: "0.0/10";
							mpa = !string.IsNullOrEmpty(omdbData.Rated) && MpaMapping.TryGetValue(omdbData.Rated, out string mappedMpa)
								? mappedMpa
								: "Unrated";

							if (omdbData.Ratings != null)
							{
								var rtRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Rotten Tomatoes");
								rottenTomatoesRating = rtRating?.Value;

								var mcRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Metacritic");
								metacriticRating = mcRating?.Value;
							}
						}
					}
				}
				var movie = new Movie
				{
					TMDBId = tmdbId,
					IMDBId = !string.IsNullOrEmpty(imdbId) ? int.Parse(imdbId.Replace("tt", "")) : 0,
					Title = movieDetails.Title,
					Overview = movieDetails.Overview,
					ReleaseDate = !string.IsNullOrEmpty(movieDetails.ReleaseDate) ? DateOnly.Parse(movieDetails.ReleaseDate) : null,
					Runtime = movieDetails.Runtime,
					Language = movieDetails.OriginalLanguage,
					PosterPath = movieDetails.PosterPath,
					BackdropPath = movieDetails.BackdropPath,
					Trailer = trailerUrl,
					Budget = movieDetails.Budget,
					Revenue = movieDetails.Revenue,
					Status = movieDetails.Status,
					Tagline = movieDetails.Tagline,
					IMDBRating = imdbRating,
					RottenTomatoesRating = rottenTomatoesRating,
					MetacriticRating = metacriticRating,
					MPA = mpa,
					Popularity = movieDetails.Popularity,
					LogoPath = logoPath,
					IsDeleted = false
				};
				await _context.Movies.AddAsync(movie, cancellationToken);

				if (movieDetails.Genres != null)
				{
					foreach (var genre in movieDetails.Genres)
					{
						var existingGenre = await _context.Genres
							.FirstOrDefaultAsync(g => g.Id == genre.Id, cancellationToken);

						if (existingGenre == null)
						{
							existingGenre = new Genre
							{
								Id = genre.Id,
								Name = genre.Name
							};
							await _context.Genres.AddAsync(existingGenre, cancellationToken);
						}
						else
							_context.Genres.Attach(existingGenre);

						var movieGenre = new MovieGenre
						{
							TMDBId = movie.TMDBId,
							GenreId = genre.Id,
							Movie = movie,
							Genre = existingGenre
						};
						await _context.MovieGenres.AddAsync(movieGenre, cancellationToken);
					}
				}
				if (credits != null)
				{
					var peopleToAdd = new List<(int Id, string Name, string Role, string Department, string ProfilePath, int? Gender, double? Popularity, bool IsCast, string Character)>();
					var castMovieCache = new HashSet<(int CastId, int TmdbId)>();

					if (credits.Cast != null)
					{
						foreach (var castMember in credits.Cast)
						{
							if (!peopleToAdd.Any(p => p.Id == castMember.Id))
							{
								peopleToAdd.Add((castMember.Id, castMember.Name, castMember.Character, castMember.KnownForDepartment,
									castMember.ProfilePath, castMember.Gender, castMember.Popularity, true, castMember.Character));
							}
						}
					}

					if (credits.Crew != null)
					{
						var importantJobs = new HashSet<string> { "Director", "Writer", "Editor", "Producer" };
						foreach (var crewMember in credits.Crew.Where(c => importantJobs.Contains(c.Job)))
						{
							if (!peopleToAdd.Any(p => p.Id == crewMember.Id))
							{
								peopleToAdd.Add((crewMember.Id, crewMember.Name, crewMember.Job, crewMember.Department,
									crewMember.ProfilePath, crewMember.Gender, crewMember.Popularity, false, crewMember.Department));
							}
						}
					}

					var topPeople = peopleToAdd
						.OrderByDescending(p => p.Popularity)
						.Take(25)
						.ToList();

					foreach (var person in topPeople)
					{
						var castMovieKey = (person.Id, movie.TMDBId);
						if (castMovieCache.Contains(castMovieKey))
						{
							_logger?.LogInformation($"Skipping duplicate CastMovie for CastId {person.Id} and TmdbId {movie.TMDBId}");
							continue;
						}

						var existingCastMovie = await _context.CastMovie
							.AsNoTracking()
							.FirstOrDefaultAsync(cm => cm.CastId == person.Id && cm.TmdbId == movie.TMDBId, cancellationToken);

						if (existingCastMovie != null)
						{
							_logger?.LogInformation($"Found existing CastMovie for CastId {person.Id} and TmdbId {movie.TMDBId}. Skipping.");
							castMovieCache.Add(castMovieKey);
							continue;
						}

						var existingCast = await _context.Casts
							.FirstOrDefaultAsync(c => c.CastId == person.Id, cancellationToken);

						if (existingCast == null)
						{
							var castDetailsResponse = await _httpClient.GetAsync($"{TmdbBaseUrl}/person/{person.Id}?api_key={TmdbApiKey}", cancellationToken);
							var castDetails = castDetailsResponse.IsSuccessStatusCode
								? await castDetailsResponse.Content.ReadFromJsonAsync<TmdbPersonResponse>(cancellationToken: cancellationToken)
								: null;

							existingCast = new Cast
							{
								CastId = person.Id,
								Name = person.Name,
								ProfilePath = person.ProfilePath,
								Gender = person.Gender,
								Popularity = person.Popularity,
								KnownForDepartment = person.Department,
								Biography = castDetails?.Biography,
								BirthDay = !string.IsNullOrEmpty(castDetails?.Birthday) ? DateOnly.Parse(castDetails.Birthday) : null,
								DeathDay = !string.IsNullOrEmpty(castDetails?.Deathday) ? DateOnly.Parse(castDetails.Deathday) : null,
								PlaceOfBirth = castDetails?.PlaceOfBirth
							};
							await _context.Casts.AddAsync(existingCast, cancellationToken);
						}
						else
						{
							_context.Casts.Attach(existingCast);
						}

						var castMovie = new CastMovie
						{
							CastId = person.Id,
							TmdbId = movie.TMDBId,
							Role = person.IsCast ? "cast" : "crew",
							Extra = person.IsCast ? person.Character : person.Department,
							Movie = movie,
							Cast = existingCast
						};
						await _context.CastMovie.AddAsync(castMovie, cancellationToken);
						castMovieCache.Add(castMovieKey);
					}
				}

				try
				{
					await _context.SaveChangesAsync(cancellationToken);
					_logger?.LogInformation($"Successfully added movie with TMDB ID {tmdbId} to database");
					return 1;
				}
				catch (DbUpdateException dbEx)
				{
					_logger?.LogError(dbEx, $"Database error while saving movie with TMDB ID {tmdbId}. Inner exception: {dbEx.InnerException?.Message}");
					return 0;
				}
				catch (Exception ex)
				{
					_logger?.LogError(ex, $"Unexpected error while saving movie with TMDB ID {tmdbId}");
					return 0;
				}
				finally
				{
					_context.ChangeTracker.Clear();
				}
			}
			catch (HttpRequestException httpEx)
			{
				_logger?.LogError(httpEx, $"HTTP error fetching data for TMDB ID {tmdbId}");
				return 0;
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, $"Error fetching and saving movie with TMDB ID {tmdbId}");
				return 0;
			}
		}
		public async Task<int> FetchMovieBasedOnPopularity(CancellationToken cancellationToken = default)
		{
			try
			{
				int targetMoviesToAdd = 1000;
				int addedMoviesCount = 0;
				int currentPage = 1;
				const int moviesPerPage = 20;

				while (addedMoviesCount < targetMoviesToAdd)
				{
					var response = await _httpClient.GetAsync(
						$"{TmdbBaseUrl}/discover/movie?api_key={TmdbApiKey}&sort_by=popularity.desc&page={currentPage}",
						cancellationToken);
					response.EnsureSuccessStatusCode();

					var content = await response.Content.ReadFromJsonAsync<TmdbUpcomingResponse>(cancellationToken: cancellationToken);

					if (content?.Results == null || !content.Results.Any())
					{
						_logger?.LogInformation("No more movies available to fetch.");
						break;
					}

					foreach (var popularMovie in content.Results)
					{
						if (addedMoviesCount >= targetMoviesToAdd)
							break;

						var existingMovie = await _context.Movies
							.FirstOrDefaultAsync(m => m.TMDBId == popularMovie.Id, cancellationToken);

						if (existingMovie != null)
						{
							_logger?.LogInformation($"Skipping existing movie {popularMovie.Id} - {popularMovie.Title}.");
							continue;
						}

						var movieDetailsResponse = await _httpClient.GetAsync(
							$"{TmdbBaseUrl}/movie/{popularMovie.Id}?api_key={TmdbApiKey}",
							cancellationToken);
						if (!movieDetailsResponse.IsSuccessStatusCode)
							continue;

						var movieDetails = await movieDetailsResponse.Content.ReadFromJsonAsync<TmdbUpcomingMovie>(cancellationToken: cancellationToken);
						if (movieDetails == null)
							continue;

						string trailerUrl = null;
						var videosResponse = await _httpClient.GetAsync(
							$"{TmdbBaseUrl}/movie/{popularMovie.Id}/videos?api_key={TmdbApiKey}",
							cancellationToken);
						if (videosResponse.IsSuccessStatusCode)
						{
							var videos = await videosResponse.Content.ReadFromJsonAsync<TmdbVideosResponse>(cancellationToken: cancellationToken);
							if (videos?.Results != null && videos.Results.Any())
							{
								var officialTrailer = videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
									v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase) &&
									v.Official) ??
									videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
									v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase)) ??
									videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
									v.Type.Equals("Teaser", StringComparison.OrdinalIgnoreCase)) ??
									videos.Results.FirstOrDefault(v =>
									v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase));

								if (officialTrailer != null)
									trailerUrl = officialTrailer.Key;
							}
						}

						var creditsResponse = await _httpClient.GetAsync(
							$"{TmdbBaseUrl}/movie/{popularMovie.Id}/credits?api_key={TmdbApiKey}",
							cancellationToken);
						var credits = creditsResponse.IsSuccessStatusCode
							? await creditsResponse.Content.ReadFromJsonAsync<TmdbCreditsResponse>(cancellationToken: cancellationToken)
							: null;

						string logoPath = null;
						var imagesResponse = await _httpClient.GetAsync(
							$"{TmdbBaseUrl}/movie/{popularMovie.Id}/images?api_key={TmdbApiKey}",
							cancellationToken);
						if (imagesResponse.IsSuccessStatusCode)
						{
							var images = await imagesResponse.Content.ReadFromJsonAsync<TmdbImagesResponse>(cancellationToken: cancellationToken);
							var englishLogo = images?.Logos?.FirstOrDefault(l => l.Iso_639_1 == "en");
							logoPath = englishLogo?.FilePath;
						}

						string imdbId = movieDetails.ImdbId;
						string imdbRating = null;
						string rottenTomatoesRating = null;
						string metacriticRating = null;
						string mpa = null;

						if (!string.IsNullOrEmpty(imdbId))
						{
							var omdbResponse = await _httpClient.GetAsync(
								$"{OmdbBaseUrl}/?i={imdbId}&apikey={OmdbApiKey}",
								cancellationToken);
							if (omdbResponse.IsSuccessStatusCode)
							{
								var omdbData = await omdbResponse.Content.ReadFromJsonAsync<OmdbResponse>(cancellationToken: cancellationToken);
								if (omdbData != null)
								{
									imdbRating = !string.IsNullOrEmpty(omdbData.ImdbRating) && omdbData.ImdbRating != "N/A"
										? omdbData.ImdbRating + "/10"
										: "0.0/10";
									mpa = omdbData.Rated;

									if (omdbData.Ratings != null)
									{
										var rtRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Rotten Tomatoes");
										rottenTomatoesRating = rtRating?.Value;

										var mcRating = omdbData.Ratings.FirstOrDefault(r => r.Source == "Metacritic");
										metacriticRating = mcRating?.Value;
									}
								}
							}
						}

						var movie = new Movie
						{
							TMDBId = popularMovie.Id,
							IMDBId = !string.IsNullOrEmpty(imdbId) ? int.Parse(imdbId.Replace("tt", "")) : 0,
							Title = popularMovie.Title,
							Overview = popularMovie.Overview,
							ReleaseDate = !string.IsNullOrEmpty(popularMovie.ReleaseDate)
								? DateOnly.Parse(popularMovie.ReleaseDate)
								: null,
							Runtime = movieDetails.Runtime,
							Language = popularMovie.OriginalLanguage,
							PosterPath = popularMovie.PosterPath,
							BackdropPath = popularMovie.BackdropPath,
							Trailer = trailerUrl,
							Budget = movieDetails.Budget,
							Revenue = movieDetails.Revenue,
							Status = movieDetails.Status,
							Tagline = movieDetails.Tagline,
							IMDBRating = imdbRating,
							RottenTomatoesRating = rottenTomatoesRating,
							MetacriticRating = metacriticRating,
							MPA = mpa,
							Popularity = popularMovie.Popularity,
							LogoPath = logoPath,
							IsDeleted = false
						};

						await _context.Movies.AddAsync(movie, cancellationToken);

						if (movieDetails.Genres != null)
						{
							foreach (var genre in movieDetails.Genres)
							{
								var existingGenre = await _context.Genres
									.FirstOrDefaultAsync(g => g.Id == genre.Id, cancellationToken);

								if (existingGenre == null)
								{
									existingGenre = new Genre
									{
										Id = genre.Id,
										Name = genre.Name
									};
									await _context.Genres.AddAsync(existingGenre, cancellationToken);
								}

								var movieGenre = new MovieGenre
								{
									TMDBId = movie.TMDBId,
									GenreId = genre.Id,
									Movie = movie,
									Genre = existingGenre
								};
								await _context.MovieGenres.AddAsync(movieGenre, cancellationToken);
							}
						}

						if (credits != null)
						{
							var peopleToAdd = new List<(int Id, string Name, string Role, string Department,
								string ProfilePath, int? Gender, double? Popularity, bool IsCast, string Character)>();

							if (credits.Cast != null)
							{
								foreach (var castMember in credits.Cast)
								{
									peopleToAdd.Add((castMember.Id, castMember.Name, castMember.Character,
										castMember.KnownForDepartment, castMember.ProfilePath, castMember.Gender,
										castMember.Popularity, true, castMember.Character));
								}
							}

							if (credits.Crew != null)
							{
								var importantJobs = new HashSet<string> { "Director", "Writer", "Editor", "Producer" };
								foreach (var crewMember in credits.Crew.Where(c => importantJobs.Contains(c.Job)))
								{
									if (!peopleToAdd.Any(p => p.Id == crewMember.Id))
									{
										peopleToAdd.Add((crewMember.Id, crewMember.Name, crewMember.Job,
											crewMember.Department, crewMember.ProfilePath, crewMember.Gender,
											crewMember.Popularity, false, crewMember.Department));
									}
								}
							}

							var topPeople = peopleToAdd
								.OrderByDescending(p => p.Popularity)
								.Take(25)
								.ToList();

							foreach (var person in topPeople)
							{
								var existingCast = await _context.Casts
									.FirstOrDefaultAsync(c => c.CastId == person.Id, cancellationToken);

								if (existingCast == null)
								{
									var castDetailsResponse = await _httpClient.GetAsync(
										$"{TmdbBaseUrl}/person/{person.Id}?api_key={TmdbApiKey}",
										cancellationToken);
									var castDetails = castDetailsResponse.IsSuccessStatusCode
										? await castDetailsResponse.Content.ReadFromJsonAsync<TmdbPersonResponse>(
											cancellationToken: cancellationToken)
										: null;

									existingCast = new Cast
									{
										CastId = person.Id,
										Name = person.Name,
										ProfilePath = person.ProfilePath,
										Gender = person.Gender,
										Popularity = person.Popularity,
										KnownForDepartment = person.Department,
										Biography = castDetails?.Biography,
										BirthDay = !string.IsNullOrEmpty(castDetails?.Birthday)
											? DateOnly.Parse(castDetails.Birthday)
											: null,
										DeathDay = !string.IsNullOrEmpty(castDetails?.Deathday)
											? DateOnly.Parse(castDetails.Deathday)
											: null,
										PlaceOfBirth = castDetails?.PlaceOfBirth
									};
									await _context.Casts.AddAsync(existingCast, cancellationToken);
								}

								var castMovie = new CastMovie
								{
									CastId = person.Id,
									TmdbId = movie.TMDBId,
									Role = person.IsCast ? "cast" : "crew",
									Extra = person.IsCast ? person.Character : person.Department,
									Movie = movie,
									Cast = existingCast
								};
								await _context.CastMovie.AddAsync(castMovie, cancellationToken);
							}
						}

						addedMoviesCount++;
						_logger?.LogInformation($"Added movie {popularMovie.Id} - {popularMovie.Title} to database");
					}

					await _context.SaveChangesAsync(cancellationToken);
					currentPage++;
				}

				_logger?.LogInformation($"Added {addedMoviesCount} new popular movies to database");
				return addedMoviesCount;
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error fetching and saving popular movies");
				return 0;
			}
		}
		private static double ParseImdbRating(string? rating)
		{
			if (string.IsNullOrEmpty(rating))
				return 0;

			var slashIndex = rating.IndexOf('/');
			if (slashIndex > 0)
			{
				var ratingValue = rating.Substring(0, slashIndex);
				if (double.TryParse(ratingValue, System.Globalization.NumberStyles.AllowDecimalPoint,
					System.Globalization.CultureInfo.InvariantCulture, out double result))
				{
					return result;
				}
			}
			return 0;
		}
		private static readonly Dictionary<string, string> MpaMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "G", "G" },
			{ "TV-G", "G" },
			{ "TV-Y", "G" },
			{ "TV-Y7", "G" },
			{ "TV-Y7-FV", "G" },
			{ "U", "G" },
			{ "E", "G" },
			{ "Approved", "G" },
			{ "Passed", "G" },
			{ "Open", "G" },
			{ "PG", "PG" },
			{ "TV-PG", "PG" },
			{ "GP", "PG" },
			{ "M/PG", "PG" },
			{ "C", "PG" },
			{ "K-8", "PG" },
			{ "Atp", "PG" },
			{ "Btl", "PG" },
			{ "EM", "PG" },
			{ "F", "PG" },
			{ "PD", "PG" },
			{ "PG-13", "PG-13" },
			{ "TV-13", "PG-13" },
			{ "TV-14", "PG-13" },
			{ "13+", "PG-13" },
			{ "12", "PG-13" },
			{ "K-13", "PG-13" },
			{ "K-14", "PG-13" },
			{ "M", "PG-13" },
			{ "R", "R" },
			{ "TV-MA", "R" },
			{ "16", "R" },
			{ "16+", "R" },
			{ "K-16", "R" },
			{ "MA-17", "R" },
			{ "NC-17", "NC-17" },
			{ "18", "NC-17" },
			{ "18+", "NC-17" },
			{ "AO", "NC-17" },
			{ "X", "NC-17" },
			{ "Banned", "NC-17" },
			{ "Unrated", "Unrated" },
			{ "Not Rated", "Unrated" },
			{ "N/A", "Unrated" },
			{ "o.Al.", "Unrated" }
		};
		private async Task<IEnumerable<MovieRecommendationResponse>?> GetMovieRecommendationsAsync(MovieRecommendationRequest request, CancellationToken cancellationToken)
{
    try
    {
        var requestBody = new MovieRecommendationApiRequest { UserId = request.UserId, Age = request.Age };
        var jsonContent = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(MovieRecommenderUrl, content, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            var apiResponse = JsonSerializer.Deserialize<MLApiResponse>(responseJson, options)
                ?? throw new JsonException($"Deserialization failed. Response: {responseJson}");

            if (apiResponse.Status != "success" || apiResponse.Data?.Recommendations == null)
                return null;

            var recommendationData = new List<(int TmdbId, double Score)>();
            foreach (var recommendation in apiResponse.Data.Recommendations)
            {
                if (recommendation.Length >= 2 &&
                    recommendation[0] is JsonElement tmdbIdElement &&
                    recommendation[1] is JsonElement scoreElement)
                {
                    if (tmdbIdElement.TryGetInt32(out int tmdbId) &&
                        scoreElement.TryGetDouble(out double score))
                    {
                        recommendationData.Add((tmdbId, score));
                    }
                }
            }
            var tmdbIds = recommendationData.Select(r => r.TmdbId).ToList();
            var movies = await _context.Movies
                .Where(m => tmdbIds.Contains(m.TMDBId) && !m.IsDeleted)
                .ToListAsync(cancellationToken);

            var recommendations = new List<MovieRecommendationResponse>();
            foreach (var (tmdbId, score) in recommendationData)
            {
                var movie = movies.FirstOrDefault(m => m.TMDBId == tmdbId);
                if (movie != null)
                {
                    recommendations.Add(new MovieRecommendationResponse(
                        movie.TMDBId,
                        movie.Title,
                        movie.PosterPath,
                        movie.IMDBRating,
                        movie.MPA,
                        score,
                        movie.ReleaseDate?.Year
                    ));
                }
            }
            return recommendations;
        }
        else
            throw new HttpRequestException($"API request failed with status {response.StatusCode}");
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Error getting movie recommendations from API");
        return null;
    }
}
		private async Task<IEnumerable<MovieSimilarityApiResponse>?> GetMovieSimilarityFromApiAsync(int tmdbId, CancellationToken cancellationToken)
		{
			try
			{
				var requestBody = new MovieSimilarityApiRequest { TMDBId = tmdbId };
				var jsonContent = JsonSerializer.Serialize(requestBody);
				var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
				var response = await _httpClient.PostAsync(MovieSimilarityUrl, content, cancellationToken);
				var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

				if (response.IsSuccessStatusCode)
				{
					var result = JsonSerializer.Deserialize<MovieSimilarityApiResponse>(responseJson, new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true,
						AllowTrailingCommas = true,
						ReadCommentHandling = JsonCommentHandling.Skip
					}) ?? throw new JsonException($"Deserialization failed. Response: {responseJson}");

					return new List<MovieSimilarityApiResponse> { result };
				}
				else
				{
					var errorResponse = await response.Content.ReadAsStringAsync(cancellationToken);
					throw new HttpRequestException($"API request failed with status {response.StatusCode}");
				}
			}
			catch (Exception ex)
			{
				return null;
			}
		}
	}
	public class TmdbCreditsResponse
	{
		[JsonPropertyName("cast")]
		public List<TmdbCastMember> Cast { get; set; } = new List<TmdbCastMember>();

		[JsonPropertyName("crew")]
		public List<TmdbCrewMember> Crew { get; set; } = new List<TmdbCrewMember>();
	}

	public class TmdbCastMember
	{
		[JsonPropertyName("id")]
		public int Id { get; set; }

		[JsonPropertyName("name")]
		public string Name { get; set; }

		[JsonPropertyName("character")]
		public string Character { get; set; }

		[JsonPropertyName("profile_path")]
		public string ProfilePath { get; set; }

		[JsonPropertyName("gender")]
		public int? Gender { get; set; }

		[JsonPropertyName("popularity")]
		public double? Popularity { get; set; }

		[JsonPropertyName("known_for_department")]
		public string KnownForDepartment { get; set; }
	}

	public class TmdbCrewMember
	{
		[JsonPropertyName("id")]
		public int Id { get; set; }

		[JsonPropertyName("name")]
		public string Name { get; set; }

		[JsonPropertyName("job")]
		public string Job { get; set; }

		[JsonPropertyName("department")]
		public string Department { get; set; }

		[JsonPropertyName("profile_path")]
		public string ProfilePath { get; set; }

		[JsonPropertyName("gender")]
		public int? Gender { get; set; }

		[JsonPropertyName("popularity")]
		public double? Popularity { get; set; }
	}

	public class TmdbPersonResponse
	{
		[JsonPropertyName("biography")]
		public string Biography { get; set; }

		[JsonPropertyName("birthday")]
		public string Birthday { get; set; }

		[JsonPropertyName("deathday")]
		public string Deathday { get; set; }

		[JsonPropertyName("place_of_birth")]
		public string PlaceOfBirth { get; set; }
	}

	public class TmdbImagesResponse
	{
		[JsonPropertyName("logos")]
		public List<TmdbLogo> Logos { get; set; } = new List<TmdbLogo>();
	}

	public class TmdbLogo
	{
		[JsonPropertyName("file_path")]
		public string FilePath { get; set; }

		[JsonPropertyName("iso_639_1")]
		public string Iso_639_1 { get; set; }
	}

	public class OmdbResponse
	{
		[JsonPropertyName("imdbRating")]
		public string ImdbRating { get; set; }

		[JsonPropertyName("Rated")]
		public string Rated { get; set; }

		[JsonPropertyName("Ratings")]
		public List<OmdbRating> Ratings { get; set; } = new List<OmdbRating>();
	}

	public class OmdbRating
	{
		[JsonPropertyName("Source")]
		public string Source { get; set; }

		[JsonPropertyName("Value")]
		public string Value { get; set; }
	}

	public class TmdbVideosResponse
	{
		[JsonPropertyName("results")]
		public List<TmdbVideo> Results { get; set; } = new List<TmdbVideo>();
	}

	public class TmdbVideo
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }

		[JsonPropertyName("key")]
		public string Key { get; set; }

		[JsonPropertyName("name")]
		public string Name { get; set; }

		[JsonPropertyName("site")]
		public string Site { get; set; }

		[JsonPropertyName("size")]
		public int Size { get; set; }

		[JsonPropertyName("type")]
		public string Type { get; set; }

		[JsonPropertyName("official")]
		public bool Official { get; set; }

		[JsonPropertyName("published_at")]
		public string PublishedAt { get; set; }
	}
	public class TmdbUpcomingResponse
	{
		[JsonPropertyName("page")]
		public int Page { get; set; }

		[JsonPropertyName("results")]
		public List<TmdbUpcomingMovie> Results { get; set; } = new List<TmdbUpcomingMovie>();

		[JsonPropertyName("total_pages")]
		public int TotalPages { get; set; }

		[JsonPropertyName("total_results")]
		public int TotalResults { get; set; }
	}
	public class TmdbTrendingResponse
	{
		[JsonPropertyName("results")]
		public List<TmdbUpcomingMovie> Results { get; set; } = new List<TmdbUpcomingMovie>();
	}

	public class TmdbUpcomingMovie
	{
		[JsonPropertyName("id")]
		public int Id { get; set; }

		[JsonPropertyName("title")]
		public string Title { get; set; }

		[JsonPropertyName("overview")]
		public string Overview { get; set; }

		[JsonPropertyName("release_date")]
		public string ReleaseDate { get; set; }

		[JsonPropertyName("runtime")]
		public int? Runtime { get; set; }

		[JsonPropertyName("original_language")]
		public string OriginalLanguage { get; set; }

		[JsonPropertyName("poster_path")]
		public string PosterPath { get; set; }

		[JsonPropertyName("backdrop_path")]
		public string BackdropPath { get; set; }

		[JsonPropertyName("budget")]
		public long Budget { get; set; }

		[JsonPropertyName("revenue")]
		public long Revenue { get; set; }

		[JsonPropertyName("status")]
		public string Status { get; set; }

		[JsonPropertyName("tagline")]
		public string Tagline { get; set; }

		[JsonPropertyName("popularity")]
		public double Popularity { get; set; }

		[JsonPropertyName("imdb_id")]
		public string ImdbId { get; set; }

		[JsonPropertyName("genres")]
		public List<TmdbGenre> Genres { get; set; } = new List<TmdbGenre>();
	}
	public class TmdbGenre
	{
		[JsonPropertyName("id")]
		public int Id { get; set; }

		[JsonPropertyName("name")]
		public string Name { get; set; }
	}

	public class MovieRecommendationApiRequest
	{
		[JsonPropertyName("user_id")]
		public string UserId { get; set; } = string.Empty;

		[JsonPropertyName("age")]
		public int Age { get; set; }
	}
	public class MovieRecommendationApiResponse
	{
		[JsonPropertyName("tmdbid")]
		public int TMDBId { get; set; }

		[JsonPropertyName("score")]
		public double Score { get; set; }
	}

	public class MovieSimilarityApiRequest
	{
		[JsonPropertyName("TMDBId")]
		public int TMDBId { get; set; }
	}
	public class MLApiResponse
	{
		[JsonPropertyName("data")]
		public MLApiResponseData? Data { get; set; }

		[JsonPropertyName("message")]
		public string? Message { get; set; }

		[JsonPropertyName("status")]
		public string? Status { get; set; }
	}
	public class MovieSimilarityApiResponse
	{
		[JsonPropertyName("data")]
		public MovieSimilarityData? Data { get; set; }

		[JsonPropertyName("message")]
		public string? Message { get; set; }

		[JsonPropertyName("status")]
		public string Status { get; set; } = string.Empty;

		public IEnumerable<int> Recommendations => Data?.Recommendations ?? Enumerable.Empty<int>();
	}

	public class MovieSimilarityData
	{
		[JsonPropertyName("recommendations")]
		public List<int> Recommendations { get; set; } = new List<int>();
	}
	public class MLApiResponseData
	{
		[JsonPropertyName("recommendations")]
		public JsonElement[][]? Recommendations { get; set; }
	}
}