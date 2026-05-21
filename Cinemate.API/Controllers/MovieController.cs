using Cinemate.Core.Service_Contract;
using Microsoft.AspNetCore.Mvc;
using Cinemate.Repository.Abstractions;
using Cinemate.Core.Contracts.Movies;
using Cinemate.Core.Contracts.Common;
using Cinemate.Core.Extensions;
using Microsoft.AspNetCore.Authorization;

namespace Cinemate.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MovieController : ControllerBase
    {
        private readonly IMovieService _movieService;
		public MovieController(IMovieService movieService)
		{
			_movieService = movieService;
		}
		[HttpGet("top-ten")]
		public async Task<IActionResult> GetTopTenMovies(CancellationToken cancellationToken)
		{ 
			var result = await _movieService.GetMovieTopTenAsync(cancellationToken);
			return Ok(result);
		}
		[HttpGet("top-rated")]
		public async Task<IActionResult> GetTopTenRatedMovies(CancellationToken cancellationToken)
		{
			var result = await _movieService.GetMovieTopTenRatedAsync(cancellationToken);
			return Ok(result);
		}
		[HttpGet("{tmdbid}")]
		public async Task<IActionResult> GetMovieDetails(int tmdbid, CancellationToken cancellationToken)
		{
			var userId = User.GetUserId();
			var result = await _movieService.GetMovieDetailsAsync(userId, tmdbid, cancellationToken);
			return result.IsSuccess ? Ok(result.Value) : result.ToProblem();
		}
		[HttpGet("random-movie")]
		public async Task<IActionResult> GetRandomMovie(CancellationToken cancellationToken)
		{
			var result = await _movieService.GetMovieRandomAsync(cancellationToken);
			return Ok(result);
		}
		[HttpGet("trending")]
		public async Task<IActionResult> GetTrendingMovies(CancellationToken cancellationToken)
		{
			var result = await _movieService.GetTrendingMoviesAsync(cancellationToken);
			return Ok(result);
		}		
		[Authorize]
		[HttpGet("recommender")]
		public async Task<IActionResult> GetMovieRecommnder(CancellationToken cancellationToken)
		{
			var result = await _movieService.GetRecommendedMoviesAsync(User.GetUserName()!,cancellationToken);
			return result.IsSuccess ? Ok(result.Value) : result.ToProblem();
		}
		[HttpGet("similarity/{tmdbId}")]
		public async Task<IActionResult> GetMovieSimilarity(int tmdbId, CancellationToken cancellationToken)
		{
			var result = await _movieService.GetMovieSimilarityAsync(tmdbId, cancellationToken);
			return result.IsSuccess ? Ok(result.Value) : result.ToProblem();
		}
		[HttpGet("genres")]
		public async Task<IActionResult> GetGenera([FromQuery] MovieGeneraRequest? request, CancellationToken cancellationToken)
		{
			var result = await _movieService.GetMovieBasedOnGeneraAsync(request, cancellationToken);
			return Ok(result);
		}
		[HttpGet("filter")]
		public async Task<IActionResult> GetPaginated([FromQuery] RequestFilters request, CancellationToken cancellationToken)
		{
			var result = await _movieService.GetPaginatedMovieBasedAsync(request, cancellationToken);
			return Ok(result);
		}
		[HttpGet("search")]
		public async Task<IActionResult> GetSearch([FromQuery] RequestSearch request, CancellationToken cancellationToken)
		{
			var result = await _movieService.GetSearchForMovieActorUsersAsync(request, cancellationToken);
			return Ok(result);
		}

		[HttpGet("fetch-upcoming")]
		public async Task<IActionResult> FetchUpcomingMovies(CancellationToken cancellationToken)
		{
			var result = await _movieService.FetchAndSaveUpcomingMoviesAsync(cancellationToken);
			return Ok(new { MoviesAdded = result });
		}
		[HttpGet("update-ratings")]
		public async Task<IActionResult> UpdateMovieRatings(CancellationToken cancellationToken)
		{
			var result = await _movieService.UpdateMovieRatingsAsync(cancellationToken);
			return Ok(new { MoviesUpdated = result });
		}
		[HttpPost("fetch-new-movie")]
		public async Task<IActionResult> FetchUpNewMovie([FromBody]int tmdbId, CancellationToken cancellationToken)
		{
			var result = await _movieService.FetchAndSaveMovieByTmdbIdAsync(tmdbId, cancellationToken);
			return Ok(new { MoviesAdded = result });
		}		
		[HttpGet("fetch-by-popularity")]
		public async Task<IActionResult> FetchByPopularity(CancellationToken cancellationToken)
		{
			var result = await _movieService.FetchMovieBasedOnPopularity(cancellationToken);
			return Ok(new { MoviesAdded = result });
		}
		[HttpPost("notify-daily-releases")]
		public async Task<IActionResult> NotifyDailyReleases(CancellationToken cancellationToken)
		{
			var result = await _movieService.UpCommingMovieAsync(cancellationToken);
			return result.IsSuccess ? Ok(new { Success = true, Message = "Daily release notifications sent successfully" }) : result.ToProblem();
		}
	}
}
