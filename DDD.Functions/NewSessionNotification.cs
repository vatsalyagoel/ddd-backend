using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DDD.Core.AzureStorage;
using DDD.Functions.Config;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DDD.Functions
{
    public static class NewSessionNotification
    {
        [FunctionName("NewSessionNotification")]
        public static async Task Run(
            [TimerTrigger("%NewSessionNotificationSchedule%")]
            TimerInfo timer,
            ILogger log,
            [BindNewSessionNotificationConfig]
            NewSessionNotificationConfig config,
            [BindSubmissionsConfig]
            SubmissionsConfig submissions)
        {
            var (submissionsRepo, submittersRepo) = await submissions.GetSubmissionRepositoryAsync();
            var notifiedSessionsRepo = await config.GetNotifiedSessionRepositoryAsync();

            var allSubmissions = await submissionsRepo.GetAllAsync(submissions.ConferenceInstance);
            var allSubmitters = await submittersRepo.GetAllAsync(submissions.ConferenceInstance);
            var notifiedSessions = await notifiedSessionsRepo.GetAllAsync();

            using (var client = new HttpClient())
            {
                foreach (var submission in allSubmissions.Where(s => notifiedSessions.All(n => n.Id != s.Id)))
                {
                    var presenterIds = submission.GetSession().PresenterIds.Select(x => x.ToString()).ToArray();
                    var presenters = allSubmitters.Where(submitter => presenterIds.Contains(submitter.Id.ToString()));
                    var postContent = JsonConvert.SerializeObject(new
                    {
                        Session = submission.GetSession(),
                        Presenters = presenters.Select(x => x.GetPresenter()).ToArray()
                    }, Formatting.None, new StringEnumConverter());

                    // Post the data
                    log.LogInformation("Posting {submissionId} to {logicAppUrl}", submission.Id, config.LogicAppUrl);
                    var response = await client.PostAsync(config.LogicAppUrl, new StringContent(postContent, Encoding.UTF8, "application/json"));
                    if (!response.IsSuccessStatusCode)
                    {
                        log.LogError("Unsuccessful request to post {documentId}; received {statusCode} and {responseBody}", submission.Id, response.StatusCode, await response.Content.ReadAsStringAsync());
                        response.EnsureSuccessStatusCode();
                    }

                    // Persist the notification record
                    await notifiedSessionsRepo.CreateAsync(new NotifiedSessionEntity(submission.Id));
                }
            }
        }
    }
}
