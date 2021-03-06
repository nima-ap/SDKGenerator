#if !NET_4_6 && (NET_2_0_SUBSET || NET_2_0)
#define TPL_35
#endif

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.AuthenticationModels;
using PlayFab.ClientModels;
using PlayFab.EventsModels;
using PlayFab.Logger;
using PlayFab.Pipeline;
using PlayFab.UUnit;

public class LightweightEventsTests : UUnitTestCase
{
    public override void ClassSetUp()
    {
        var testTitleData = TestTitleDataLoader.LoadTestTitleData();
        PlayFabSettings.TitleId = testTitleData.titleId;
    }
    public override void Tick(UUnitTestContext testContext)
    {
        // no async work needed
    }

    [UUnitTest]
    public void WriteOneDSEvents(UUnitTestContext testContext)
    {
        // get Entity Token which is needed for work events
        PlayFabClientAPI.LoginWithCustomID(new LoginWithCustomIDRequest
        {
            CustomId = PlayFabSettings.BuildIdentifier,
            TitleId = PlayFabSettings.TitleId
        }, loginCallback =>
        {
            PlayFabAuthenticationAPI.GetEntityToken(new GetEntityTokenRequest
            {
                AuthenticationContext = loginCallback.AuthenticationContext
            }, entityCallback =>
            {
#if TPL_35
                Task.Run(() => WriteOneDSEventsAsync(testContext));
#else
                WriteOneDSEventsAsync(testContext);
#endif
            }, entityError =>
            {
                testContext.Skip("EntityToken required!");
            });
        }, loginError =>
        {
            testContext.Skip("Login required!");
        });
    }

    /// <summary>
    /// ONEDS API
    /// Test that a batch of custom events can be sent to OneDS server
    /// </summary>
#if TPL_35
    public void WriteOneDSEventsAsync(UUnitTestContext testContext)
#else
    public async void WriteOneDSEventsAsync(UUnitTestContext testContext)
#endif
    {
        var event1 = new PlayFabEvent() { Name = "Event_1", EventType = PlayFabEventType.Lightweight };
        event1.SetProperty("Prop-A", true);
        event1.SetProperty("Prop-B", "hello");
        event1.SetProperty("Prop-C", 123);

        var event2 = new PlayFabEvent() { Name = "Event_2", EventType = PlayFabEventType.Lightweight };
        event2.SetProperty("Prop-A", false);
        event2.SetProperty("Prop-B", "good-bye");
        event2.SetProperty("Prop-C", 456);

        var request = new WriteEventsRequest
        {
            Events = new List<EventContents>
            {
                event1.EventContents,
                event2.EventContents
            }
        };

        var oneDSEventsApi = new OneDSEventsAPI();

        // get OneDS authentication from PlayFab
        var configRequest = new TelemetryIngestionConfigRequest();
#if TPL_35
        var authTask = OneDSEventsAPI.GetTelemetryIngestionConfigAsync(configRequest).Await();
#else
        var authTask = await OneDSEventsAPI.GetTelemetryIngestionConfigAsync(configRequest);
#endif

        var response = authTask.Result;

        testContext.NotNull(authTask.Result, "Failed to get OneDS authentication info from PlayFab");
        oneDSEventsApi.SetCredentials("o:" + authTask.Result.TenantId, authTask.Result.IngestionKey, authTask.Result.TelemetryJwtToken, authTask.Result.TelemetryJwtHeaderKey, authTask.Result.TelemetryJwtHeaderPrefix);

        // call OneDS events API
#if TPL_35
        var writeTask = oneDSEventsApi.WriteTelemetryEventsAsync(request, null, new Dictionary<string, string>()).Await();
#else
        var writeTask = await oneDSEventsApi.WriteTelemetryEventsAsync(request, null, new Dictionary<string, string>());
#endif

        testContext.NotNull(writeTask);
        testContext.IsNull(writeTask.Error, "Failed to send a batch of custom OneDS events");
        testContext.NotNull(writeTask.Result, "Failed to send a batch of custom OneDS events. Result is null!");
        testContext.EndTest(UUnitFinishState.PASSED, "");
    }


    [UUnitTest]
    public void EmitLightweightEvents(UUnitTestContext testContext)
    {
#if TPL_35
        Task.Run(() => EmitLightweightEventsAsync(testContext));
#else
        EmitLightweightEventsAsync(testContext);
#endif
    }

#if TPL_35
    public  void EmitLightweightEventsAsync(UUnitTestContext testContext)
#else
    public async void EmitLightweightEventsAsync(UUnitTestContext testContext)
#endif
    {
        // create and set settings for OneDS event pipeline
        var settings = new OneDSEventPipelineSettings();
        settings.BatchSize = 8;
        settings.BatchFillTimeout = TimeSpan.FromSeconds(1);
        var logger = new DebugLogger();

        // create OneDS event pipeline
        var oneDSPipeline = new OneDSEventPipeline(settings, logger);

        // create custom event API, add the pipeline
        var playFabEventApi = new PlayFabEventAPI(logger);
#pragma warning disable 4014
        playFabEventApi.EventRouter.AddAndStartPipeline(EventPipelineKey.OneDS, oneDSPipeline);
#pragma warning restore 4014

        // create and emit many lightweight events
#if TPL_35
        var results = new List<Task<IPlayFabEmitEventResponse>>();
#else
        var results = new List<Task>();
#endif

        for (int i = 0; i < 50; i++)
        {
            results.AddRange(playFabEventApi.EmitEvent(CreateSamplePlayFabEvent("Event_Custom", PlayFabEventType.Lightweight)));
        }

        // wait when the pipeline finishes sending all events
#if TPL_35
        Task.WhenAll(results).Await();
#else
        await Task.WhenAll(results);
#endif

        // check results
        var sentBatches = new Dictionary<IList<IPlayFabEmitEventRequest>, int>();

        foreach (var result in results)
        {
            testContext.True(result.IsCompleted, "Custom event emission task failed to complete");
            PlayFabEmitEventResponse response = (PlayFabEmitEventResponse)((Task<IPlayFabEmitEventResponse>)result).Result;
            testContext.True(response.EmitEventResult == EmitEventResult.Success, "Custom event emission task failed to succeed");
            testContext.True(response.PlayFabError == null && response.WriteEventsResponse != null, "Custom event failed to be sent");

            if (!sentBatches.ContainsKey(response.Batch))
                sentBatches[response.Batch] = 0;

            sentBatches[response.Batch]++;
        }

        int event8 = 0;
        int event2 = 0;

        foreach (var batch in sentBatches)
        {
            if (batch.Value == 8) event8++;
            else if (batch.Value == 2) event2++;
        }

        // 6 full batches of 8 events and 1 incomplete batch of 2 events are expected
        testContext.True(event8 == 6, "Wrong number of full batches");
        testContext.True(event2 == 1, "Wrong number of incomplete batches");
        testContext.EndTest(UUnitFinishState.PASSED, null);
    }

    private PlayFabEvent CreateSamplePlayFabEvent(string name, PlayFabEventType eventType)
    {
        var customEvent = new PlayFabEvent { Name = name, EventType = eventType };
        customEvent.SetProperty("Prop-A", true);
        customEvent.SetProperty("Prop-B", "custom");
        customEvent.SetProperty("Prop-C", 567);

        return customEvent;
    }
}
