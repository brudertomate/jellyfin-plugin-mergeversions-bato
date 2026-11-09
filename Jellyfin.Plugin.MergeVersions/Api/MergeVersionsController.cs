using System.Net.Mime;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions.Api
{
    /// <summary>
    /// The Merge Versions api controller.
    /// </summary>
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("MergeVersionsBato")]
    [Produces(MediaTypeNames.Application.Json)]
    public class MergeVersionsController : ControllerBase
    {
        private readonly MergeVersionsManager _mergeVersionsManager;
        private readonly ILogger<MergeVersionsManager> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="MergeVersionsController"/>.
        /// </summary>
        public MergeVersionsController(
            MergeVersionsManager mergeVersionsManager,
            ILogger<MergeVersionsManager> logger
        )
        {
            _mergeVersionsManager = mergeVersionsManager;

            _logger = logger;
        }

        /// <summary>
        /// Scans all movies and merges repeated ones.
        /// </summary>
        /// <reponse code="204">Library scan and merge started successfully. </response>
        /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
        [HttpPost("MergeMovies")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> MergeMoviesRequestAsync()
        {
            _logger.LogInformation("Starting a manual refresh, looking up for repeated versions");
            await _mergeVersionsManager.MergeMoviesAsync(null, User, HttpContext.RequestAborted);
            _logger.LogInformation("Completed refresh");
            return NoContent();
        }

        /// <summary>
        /// Gets what the latest merge or split run did: counters, the movie in progress and the log entries
        /// newer than <paramref name="after"/>. Kept in memory until Jellyfin restarts.
        /// </summary>
        /// <param name="after">Only return log entries with a higher sequence number.</param>
        /// <response code="200">The current or latest run.</response>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<ActivitySnapshot> GetStatus([FromQuery] long after = 0)
        {
            return _mergeVersionsManager.Activity.GetSnapshot(after);
        }

        /// <summary>
        /// Scans all movies and splits merged ones.
        /// </summary>
        /// <reponse code="204">Library scan and split started successfully. </response>
        /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
        [HttpPost("SplitMovies")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> SplitMoviesRequestAsync()
        {
            _logger.LogInformation("Splitting all movies");
            await _mergeVersionsManager.SplitMoviesAsync(null, User, HttpContext.RequestAborted);
            _logger.LogInformation("Completed");
            return NoContent();
        }
    }
}
