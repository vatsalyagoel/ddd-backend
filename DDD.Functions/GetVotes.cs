using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage;
using DDD.Core.DocumentDb;
using DDD.Sessionize;
using DDD.Functions.Config;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Linq;
using System;
using System.Collections.Generic;
using DDD.Core.AzureStorage;

namespace DDD.Functions
{
    public static class GetVotes
    {
        [FunctionName("GetVotes")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)]
            HttpRequest req,
            TraceWriter log,
            [BindGetVotesConfig]
            GetVotesConfig config)
        {
            // Get sessions
            var documentDbClient = DocumentDbAccount.Parse(config.SessionsConnectionString);
            var repo = new DocumentDbRepository<SessionOrPresenter>(documentDbClient, config.CosmosDatabaseId, config.CosmosCollectionId);
            await repo.InitializeAsync();
            var all = (await repo.GetAllItemsAsync()).ToArray();

            // Get votes
            var account = CloudStorageAccount.Parse(config.VotingConnectionString);
            var table = account.CreateCloudTableClient().GetTableReference(config.VotingTable);
            await table.CreateIfNotExistsAsync();
            var votes = await table.GetAllByPartitionKeyAsync<Vote>(config.ConferenceInstance);

            // Get Eventbrite ids
            var ebTable = account.CreateCloudTableClient().GetTableReference(config.EventbriteTable);
            var eventbriteOrders = await ebTable.GetAllByPartitionKeyAsync<EventbriteOrder>(config.ConferenceInstance);
            var eventbriteIds = eventbriteOrders.Select(o => o.OrderId).ToArray();

            // Get AppInsights sessions
            var aiTable = account.CreateCloudTableClient().GetTableReference(config.AppInsightsTable);
            var userSessions = await aiTable.GetAllByPartitionKeyAsync<AppInsightsVotingUser>(config.ConferenceInstance);

            // Analyse votes
            var analysedVotes = votes.Select(v => new AnalysedVote(v, votes, eventbriteIds, userSessions)).ToArray();

            // Get summary
            var presenters = all.Where(x => x.Presenter != null).Select(x => x.Presenter).ToArray();
            var sessions = all.Where(x => x.Session != null)
                .Select(x => x.Session)
                .Select(s => new SessionWithVotes
                {
                    Id = s.Id.ToString(),
                    Title = s.Title,
                    Abstract = s.Abstract,
                    Format = s.Format,
                    Level = s.Level,
                    Tags = s.Tags,
                    Presenters = s.PresenterIds.Select(pId => presenters.Where(p => p.Id == pId).Select(p => new Presenter
                        {
                            Id = p.Id.ToString(),
                            Name = p.Name,
                            Tagline = p.Tagline,
                            Bio = p.Bio,
                            ProfilePhotoUrl = p.ProfilePhotoUrl,
                            TwitterHandle = p.TwitterHandle,
                            WebsiteUrl = p.WebsiteUrl
                        }).Single()).ToArray(),
                    CreatedDate = s.CreatedDate,
                    ModifiedDate = s.ModifiedDate,
                    IsUnderrepresented = s.DataFields.ContainsKey("Are you a member of any underrepresented groups?") && !string.IsNullOrEmpty(s.DataFields["Are you a member of any underrepresented groups?"]),
                    Pronoun = s.DataFields["Your preferred pronoun"],
                    JobRole = s.DataFields["How would you identify your job role?"],
                    SpeakingExperience = s.DataFields["How much speaking experience do you have?"],
                    VoteSummary = new VoteSummary(analysedVotes.Where(v => v.Vote.GetSessionIds().Contains(s.Id.ToString())).ToArray())
                })
                .OrderBy(s => s.Title)
                .ToArray();

            var tagSummaries = sessions.SelectMany(s => s.Tags).Distinct().OrderBy(t => t)
                .Select(tag => new TagSummary
                {
                    Tag = tag,
                    VoteSummary = new VoteSummary(sessions.Where(s => s.Tags.Contains(tag)).SelectMany(s => analysedVotes.Where(v => v.Vote.SessionIds.Contains(s.Id))).ToArray())
                }).ToArray();

            var response = new GetVotesResponse
            {
                VoteSummary = new VoteSummary(analysedVotes),
                Sessions = sessions.OrderByDescending(s => s.VoteSummary.RawTotal).ToArray(),
                TagSummaries = tagSummaries,
                Votes = analysedVotes,
                UserSessions = userSessions.Select(x => new UserSession{UserId = x.UserId, VoteId = x.VoteId, StartTime = x.StartTime}).ToArray()
            };
            var settings = new JsonSerializerSettings {ContractResolver = new DefaultContractResolver()};

            return new JsonResult(response, settings);
        }
    }

    public class VoteSummary
    {
        public VoteSummary(AnalysedVote[] votes)
        {
            RawTotal = votes.Length;
            TotalWithValidTicketNumber = votes.Count(v => v.HasValidTicketNumber);
            TotalWithInvalidTicketNumber = votes.Count(v => v.HasTicketNumber && !v.HasValidTicketNumber);
            TotalWithDuplicateTicketNumber = votes.Count(v => v.HasDuplicateTicketNumber);
            TotalWithValidAppInsightsId = votes.Count(v => v.HasValidAppInsightsId);
            TotalWithInvalidAppInsightsId = votes.Count(v => v.HasAppInsightsId && !v.HasValidAppInsightsId);
            TotalWithDuplicateAppInsightsId = votes.Count(v => v.HasDuplicateAppInsightsId);
            TotalUniqueIpAddresses = votes.Select(v => v.Vote.IpAddress).Distinct().Count();
            TotalSessionsVoted = votes.Sum(v => v.Vote.GetSessionIds().Length);
        }

        public int RawTotal { get; }
        public int TotalWithValidTicketNumber { get; }
        public int TotalWithInvalidTicketNumber { get; }
        public int TotalWithDuplicateTicketNumber { get; }
        public int TotalWithValidAppInsightsId { get; }
        public int TotalWithInvalidAppInsightsId { get; }
        public int TotalWithDuplicateAppInsightsId { get; }
        public int TotalUniqueIpAddresses { get; }
        public int TotalSessionsVoted { get; }
    }

    public class TagSummary
    {
        public string Tag { get; set; }
        public VoteSummary VoteSummary { get; set; }
    }

    public class AnalysedVote : IEquatable<AnalysedVote>
    {
        public AnalysedVote(Vote vote, IList<Vote> allVotes, IList<string> validTicketNumbers,
            List<AppInsightsVotingUser> userSessions)
        {
            var orderedIndices = vote.GetIndices().Select(int.Parse).OrderBy(x => x).ToArray();
            var indexGaps = orderedIndices.Select((index, i) => i == 0 ? 0 : index - orderedIndices[i - 1]).Skip(1);

            Vote = vote;
            HasTicketNumber = !string.IsNullOrEmpty(vote.TicketNumber);
            HasValidTicketNumber = HasTicketNumber && validTicketNumbers.Contains(vote.TicketNumber);
            HasDuplicateTicketNumber = HasValidTicketNumber && allVotes.Any(v => v.VoteId != vote.VoteId && v.TicketNumber == vote.TicketNumber);

            HasAppInsightsId = !string.IsNullOrEmpty(vote.VoterSessionId);
            HasValidAppInsightsId = HasAppInsightsId && userSessions.Any(x => x.UserId == vote.VoterSessionId && x.VoteId == vote.VoteId);
            HasDuplicateAppInsightsId = HasAppInsightsId && allVotes.Any(v => v.VoteId != vote.VoteId && v.VoterSessionId == vote.VoterSessionId);
            IndexGaps = indexGaps.ToArray();
        }

        public Vote Vote { get; }
        public bool HasTicketNumber { get; }
        public bool HasValidTicketNumber { get; }
        public bool HasDuplicateTicketNumber { get; }
        public bool HasAppInsightsId { get; }
        public bool HasValidAppInsightsId { get; }
        public bool HasDuplicateAppInsightsId { get; }
        public int[] IndexGaps { get; }

        public bool Equals(AnalysedVote other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Vote.VoteId, other.Vote.VoteId);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Vote.VoteId.Equals(((AnalysedVote) obj).Vote.VoteId);
        }

        public override int GetHashCode()
        {
            return (Vote != null ? Vote.GetHashCode() : 0);
        }
    }

    public class SessionWithVotes
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Abstract { get; set; }
        public string Format { get; set; }
        public string Level { get; set; }
        public string[] Tags { get; set; }
        public Presenter[] Presenters { get; set; }
        public DateTimeOffset CreatedDate { get; set; }
        public DateTimeOffset? ModifiedDate { get; set; }
        public bool IsUnderrepresented { get; set; }
        public string Pronoun { get; set; }
        public string JobRole { get; set; }
        public string SpeakingExperience { get; set; }

        public VoteSummary VoteSummary { get; set; }
    }

    public class Presenter
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Tagline { get; set; }
        public string Bio { get; set; }
        public string ProfilePhotoUrl { get; set; }
        public string TwitterHandle { get; set; }
        public string WebsiteUrl { get; set; }
    }

    public class UserSession
    {
        public string UserId { get; set; }
        public string VoteId { get; set; }
        public string StartTime { get; set; }
    }

    public class GetVotesResponse
    {
        public VoteSummary VoteSummary { get; set; }
        public TagSummary[] TagSummaries { get; set; }
        public SessionWithVotes[] Sessions { get; set; }
        public AnalysedVote[] Votes { get; set; }
        public UserSession[] UserSessions { get; set; }
    }
}
