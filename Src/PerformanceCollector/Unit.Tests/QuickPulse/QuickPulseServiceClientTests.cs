﻿namespace Unit.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Runtime.Serialization.Json;
    using System.Threading.Tasks;

    using Microsoft.ApplicationInsights.Extensibility.Filtering;
    using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.Implementation;
    using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.Implementation.QuickPulse;
    using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.Implementation.QuickPulse.Helpers;
    using Microsoft.ManagementServices.RealTimeDataProcessing.QuickPulseService;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public sealed class QuickPulseServiceClientTests : IDisposable
    {
        private const int Port = 49152 + 11;

        private readonly Uri serviceEndpoint = new Uri(string.Format(CultureInfo.InvariantCulture, "http://localhost:{0}", Port));

        /// <summary>
        /// Tuple of (Timestamp, CollectionConfigurationETag, MonitoringDataPoint).
        /// </summary>
        private readonly List<Tuple<DateTimeOffset, string, MonitoringDataPoint>> samples = new List<Tuple<DateTimeOffset, string, MonitoringDataPoint>>();

        private readonly List<Tuple<PingHeaders, string, MonitoringDataPoint>> pings = new List<Tuple<PingHeaders, string, MonitoringDataPoint>>();

        private readonly CollectionConfiguration emptyCollectionConfiguration;

        private Action<HttpListenerResponse> pingResponse;

        private Action<HttpListenerResponse> submitResponse;

        private HttpListener listener;

        private int pingCount;

        private int submitCount;

        private DateTimeOffset? lastPingTimestamp;

        private string lastPingInstance;

        private string lastVersion;

        private bool emulateTimeout;

        public QuickPulseServiceClientTests()
        {
            string[] errors;
            this.emptyCollectionConfiguration =
                new CollectionConfiguration(
                    new CollectionConfigurationInfo() { ETag = string.Empty, Metrics = new OperationalizedMetricInfo[0] },
                    out errors);
        }

        [TestInitialize]
        public void TestInitialize()
        {
            this.pingCount = 0;
            this.submitCount = 0;
            this.lastPingTimestamp = null;
            this.lastPingInstance = string.Empty;
            this.lastVersion = string.Empty;
            this.samples.Clear();
            this.emulateTimeout = false;

            this.pingResponse = response =>
                {
                    response.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString());
                };

            this.submitResponse = response =>
                {
                    response.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString());
                };

            string uriPrefix = string.Format(CultureInfo.InvariantCulture, "http://localhost:{0}/", Port);

            this.listener = new HttpListener();
            this.listener.Prefixes.Add(uriPrefix);
            this.listener.Start();

            Task.Factory.StartNew(() => this.ProcessRequest(this.listener));
        }

        [TestCleanup]
        public void TestCleanup()
        {
            this.listener.Stop();
            this.listener.Close();
        }

        [TestMethod]
        public void QuickPulseServiceClientPingsTheService()
        {
            // ARRANGE
            string instance = Guid.NewGuid().ToString();
            var timestamp = DateTimeOffset.UtcNow;

            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, instance, instance, instance, string.Empty, new Clock(), false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping(string.Empty, timestamp, string.Empty, out configurationInfo);
            serviceClient.Ping(string.Empty, timestamp, string.Empty, out configurationInfo);
            serviceClient.Ping(string.Empty, timestamp, string.Empty, out configurationInfo);

            // ASSERT
            Assert.AreEqual(3, this.pingCount);
            Assert.AreEqual(timestamp.DateTime.ToLongTimeString(), this.lastPingTimestamp.Value.DateTime.ToLongTimeString());
            Assert.AreEqual(instance, this.lastPingInstance);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsSamplesToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);
            var sample1 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { AIRequestSuccessCount = 5, StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            var sample2 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { AIDependencyCallSuccessCount = 10, StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            var sample3 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { AIExceptionCount = 15, StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            bool? sendMore = serviceClient.SubmitSamples(
                new[] { sample1, sample2, sample3 },
                string.Empty,
                string.Empty,
                out configurationInfo,
                new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(true, sendMore);
            Assert.AreEqual(3, this.samples.Count);
            Assert.AreEqual(5, this.samples[0].Item3.Metrics.Single(m => m.Name == @"\ApplicationInsights\Requests Succeeded/Sec").Value);
            Assert.AreEqual(10, this.samples[1].Item3.Metrics.Single(m => m.Name == @"\ApplicationInsights\Dependency Calls Succeeded/Sec").Value);
            Assert.AreEqual(15, this.samples[2].Item3.Metrics.Single(m => m.Name == @"\ApplicationInsights\Exceptions/Sec").Value);
        }

        [TestMethod]
        public void QuickPulseServiceClientSetsTransmissionTimeCorrectly()
        {
            // ARRANGE
            var dummy = new Dictionary<string, Tuple<PerformanceCounterData, double>>();
            var dummy2 = Enumerable.Empty<Tuple<string, int>>();
            var timeProvider = new ClockMock();

            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, timeProvider, false);
            var sample1 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = timeProvider.UtcNow.AddSeconds(-1), EndTimestamp = timeProvider.UtcNow },
                    dummy,
                    dummy2,
                    false);

            timeProvider.FastForward(TimeSpan.FromSeconds(1));
            var sample2 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = timeProvider.UtcNow.AddSeconds(-1), EndTimestamp = timeProvider.UtcNow },
                    dummy,
                    dummy2,
                    false);

            timeProvider.FastForward(TimeSpan.FromSeconds(1));
            var sample3 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = timeProvider.UtcNow.AddSeconds(-1), EndTimestamp = timeProvider.UtcNow },
                    dummy,
                    dummy2,
                    false);

            // ACT
            timeProvider.FastForward(TimeSpan.FromSeconds(5));
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample1, sample2, sample3 }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(3, this.samples.Count);
            Assert.IsTrue((timeProvider.UtcNow - this.samples[0].Item1).Duration() < TimeSpan.FromMilliseconds(1));
            Assert.IsTrue((timeProvider.UtcNow - this.samples[1].Item1).Duration() < TimeSpan.FromMilliseconds(1));
            Assert.IsTrue((timeProvider.UtcNow - this.samples[2].Item1).Duration() < TimeSpan.FromMilliseconds(1));
            Assert.IsTrue(this.samples.All(s => (s.Item3.Timestamp - timeProvider.UtcNow).Duration() > TimeSpan.FromSeconds(1)));
        }

        [TestMethod]
        public void QuickPulseServiceClientRoundsSampleValuesWhenSubmittingToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);
            var sample1 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { AIRequestSuccessCount = 1, StartTimestamp = now, EndTimestamp = now.AddSeconds(3) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample1 }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(0.3333, this.samples[0].Item3.Metrics.Single(m => m.Name == @"\ApplicationInsights\Requests Succeeded/Sec").Value);
        }

        [TestMethod]
        public void QuickPulseServiceClientFillsInSampleWeightWhenSubmittingToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);
            var sample1 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration)
                    {
                            AIRequestCountAndDurationInTicks = QuickPulseDataAccumulator.EncodeCountAndDuration(3, 10000),
                            StartTimestamp = now,
                            EndTimestamp = now.AddSeconds(1)
                        },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            var sample2 =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration)
                    {
                            AIDependencyCallCountAndDurationInTicks =
                                QuickPulseDataAccumulator.EncodeCountAndDuration(4, 10000),
                            StartTimestamp = now,
                            EndTimestamp = now.AddSeconds(1)
                        },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample1, sample2 }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(3, this.samples[0].Item3.Metrics.Single(m => m.Name == @"\ApplicationInsights\Request Duration").Weight);
            Assert.AreEqual(4, this.samples[1].Item3.Metrics.Single(m => m.Name == @"\ApplicationInsights\Dependency Call Duration").Weight);
        }

        [TestMethod]
        public void QuickPulseServiceClientFillsInTelemetryDocumentsWhenSubmittingToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);
            var properties = new Dictionary<string, string>() { { "Prop1", "Val1" } };
            var sample =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration)
                    {
                            StartTimestamp = now,
                            EndTimestamp = now.AddSeconds(1),
                            TelemetryDocuments =
                                new ConcurrentStack<ITelemetryDocument>(
                                new ITelemetryDocument[]
                                    {
                                        new RequestTelemetryDocument() { Name = "Request1", Properties = properties.ToArray() },
                                        new DependencyTelemetryDocument() { Name = "Dependency1", Properties = properties.ToArray() },
                                        new ExceptionTelemetryDocument() { Exception = "Exception1", Properties = properties.ToArray() }
                                    })
                        },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(), 
                    false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual("Request1", ((RequestTelemetryDocument)this.samples[0].Item3.Documents[0]).Name);
            Assert.AreEqual("Prop1", ((RequestTelemetryDocument)this.samples[0].Item3.Documents[0]).Properties.First().Key);
            Assert.AreEqual("Val1", ((RequestTelemetryDocument)this.samples[0].Item3.Documents[0]).Properties.First().Value);

            Assert.AreEqual("Dependency1", ((DependencyTelemetryDocument)this.samples[0].Item3.Documents[1]).Name);
            Assert.AreEqual("Prop1", ((DependencyTelemetryDocument)this.samples[0].Item3.Documents[1]).Properties.First().Key);
            Assert.AreEqual("Val1", ((DependencyTelemetryDocument)this.samples[0].Item3.Documents[1]).Properties.First().Value);

            Assert.AreEqual("Exception1", ((ExceptionTelemetryDocument)this.samples[0].Item3.Documents[2]).Exception);
            Assert.AreEqual("Prop1", ((ExceptionTelemetryDocument)this.samples[0].Item3.Documents[2]).Properties.First().Key);
            Assert.AreEqual("Val1", ((ExceptionTelemetryDocument)this.samples[0].Item3.Documents[2]).Properties.First().Value);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsPingResponseCorrectlyWhenHeaderTrue()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.pingResponse = r => { r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString()); };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.Ping(string.Empty, DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.IsTrue(response.Value);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsPingResponseCorrectlyWhenHeaderFalse()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.pingResponse = r => { r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, false.ToString()); };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.Ping(string.Empty, DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.IsFalse(response.Value);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsPingResponseCorrectlyWhenHeaderInvalid()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.pingResponse = r => { r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, "bla"); };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.Ping(string.Empty, DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(response);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsPingResponseCorrectlyWhenHeaderMissing()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.pingResponse = r => { };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.Ping(string.Empty, DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(response);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsSubmitSamplesResponseCorrectlyWhenHeaderTrue()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.submitResponse = r => { r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString()); };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.SubmitSamples(new QuickPulseDataSample[] { }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.IsTrue(response.Value);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsSubmitSamplesResponseCorrectlyWhenHeaderFalse()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.submitResponse = r => { r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, false.ToString()); };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.SubmitSamples(new QuickPulseDataSample[] { }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.IsFalse(response.Value);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsSubmitSamplesResponseCorrectlyWhenHeaderInvalid()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.submitResponse = r => { r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, "bla"); };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.SubmitSamples(new QuickPulseDataSample[] { }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(response);
        }

        [TestMethod]
        public void QuickPulseServiceClientInterpretsSubmitSamplesResponseCorrectlyWhenHeaderMissing()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);

            // ACT
            this.submitResponse = r => { };
            CollectionConfigurationInfo configurationInfo;
            bool? response = serviceClient.SubmitSamples(new QuickPulseDataSample[] { }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(response);
        }

        [TestMethod]
        public void QuickPulseServiceClientDoesNotRetryPing()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false,
                TimeSpan.FromMilliseconds(50));

            this.emulateTimeout = true;

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping(string.Empty, DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.pingCount);
        }

        [TestMethod]
        public void QuickPulseServiceClientDoesNotRetrySubmitSamples()
        {
            // ARRANGE
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false,
                TimeSpan.FromMilliseconds(50));

            this.emulateTimeout = true;

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new QuickPulseDataSample[] { }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.submitCount);
        }

        [TestMethod]
        public void QuickPulseServiceClientDoesNotReadCollectionConfigurationFromPingWhenNotSubscribed()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false);

            this.pingResponse = r =>
                {
                    r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, false.ToString());
                    r.AddHeader(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, "ETag2");

                    var collectionConfigurationInfo = new CollectionConfigurationInfo()
                                                          {
                                                              ETag = "ETag2",
                                                              Metrics =
                                                                  new[] { new OperationalizedMetricInfo() { Id = "Id1" } }
                                                          };

                    var serializer = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));
                    serializer.WriteObject(r.OutputStream, collectionConfigurationInfo);
                };

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping("ikey", now, "ETag1", out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(configurationInfo);
        }

        [TestMethod]
        public void QuickPulseServiceClientDoesNotReadCollectionConfigurationFromPostWhenNotSubscribed()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false);

            var sample =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(3) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            this.submitResponse = r =>
                {
                    r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, false.ToString());
                    r.AddHeader(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, "ETag2");

                    var collectionConfigurationInfo = new CollectionConfigurationInfo()
                                                          {
                                                              ETag = "ETag2",
                                                              Metrics =
                                                                  new[] { new OperationalizedMetricInfo() { Id = "Id1" } }
                                                          };

                    var serializer = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));
                    serializer.WriteObject(r.OutputStream, collectionConfigurationInfo);
                };

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, "ETag1", out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(configurationInfo);
        }

        [TestMethod]
        public void QuickPulseServiceClientReadsCollectionConfigurationFromPingWhenETagIsDifferent()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false);

            this.pingResponse = r =>
                {
                    r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString());
                    r.AddHeader(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, "ETag2");

                    var collectionConfigurationInfo = new CollectionConfigurationInfo()
                                                          {
                                                              ETag = "ETag2",
                                                              Metrics =
                                                                  new[] { new OperationalizedMetricInfo() { Id = "Id1" } }
                                                          };

                    var serializer = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));
                    serializer.WriteObject(r.OutputStream, collectionConfigurationInfo);
                };

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping("ikey", now, "ETag1", out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual("ETag2", configurationInfo.ETag);
            Assert.AreEqual("Id1", configurationInfo.Metrics.Single().Id);
        }

        [TestMethod]
        public void QuickPulseServiceClientReadsCollectionConfigurationFromPostWhenETagIsDifferent()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false);

            var sample =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(3) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            this.submitResponse = r =>
                {
                    r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString());
                    r.AddHeader(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, "ETag2");

                    var collectionConfigurationInfo = new CollectionConfigurationInfo()
                                                          {
                                                              ETag = "ETag2",
                                                              Metrics =
                                                                  new[] { new OperationalizedMetricInfo() { Id = "Id1" } }
                                                          };

                    var serializer = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));
                    serializer.WriteObject(r.OutputStream, collectionConfigurationInfo);
                };

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, "ETag1", out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual("ETag2", configurationInfo.ETag);
            Assert.AreEqual("Id1", configurationInfo.Metrics.Single().Id);
        }

        [TestMethod]
        public void QuickPulseServiceClientDoesNotReadCollectionConfigurationFromPingWhenETagIsSame()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false);

            this.pingResponse = r =>
                {
                    r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString());
                    r.AddHeader(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, "ETag2");

                    var collectionConfigurationInfo = new CollectionConfigurationInfo() { ETag = "ETag2", Metrics = null };

                    var serializer = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));
                    serializer.WriteObject(r.OutputStream, collectionConfigurationInfo);
                };

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping("ikey", now, "ETag2", out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(configurationInfo);
        }

        [TestMethod]
        public void QuickPulseServiceClientDoesNotReadCollectionConfigurationFromPostWhenETagIsSame()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false);

            var sample =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(3) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            this.submitResponse = r =>
                {
                    r.AddHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName, true.ToString());
                    r.AddHeader(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, "ETag2");

                    var collectionConfigurationInfo = new CollectionConfigurationInfo() { ETag = "ETag2", Metrics = null };

                    var serializer = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));
                    serializer.WriteObject(r.OutputStream, collectionConfigurationInfo);
                };

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, "ETag2", out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.IsNull(configurationInfo);
        }

        [TestMethod]
        public void QuickPulseServiceClientProducesOperationalizedMetricsCorrectly()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(
                this.serviceEndpoint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Clock(),
                false);

            var metrics = new[]
                              {
                                  new OperationalizedMetricInfo()
                                      {
                                          SessionId = "Session1",
                                          Id = "Metric1",
                                          TelemetryType = TelemetryType.Request,
                                          Projection = "Id",
                                          Aggregation = AggregationType.Avg,
                                          Filters = new FilterInfo[0]
                                      },
                                  new OperationalizedMetricInfo()
                                      {
                                          SessionId = "Session2",
                                          Id = "Metric2",
                                          TelemetryType = TelemetryType.Request,
                                          Projection = "Id",
                                          Aggregation = AggregationType.Avg,
                                          Filters = new FilterInfo[0]
                                      },
                                   new OperationalizedMetricInfo()
                                      {
                                          SessionId = "Session3",
                                          Id = "Metric3",
                                          TelemetryType = TelemetryType.Request,
                                          Projection = "Id",
                                          Aggregation = AggregationType.Sum,
                                          Filters = new FilterInfo[0]
                                      }
                              };

            string[] errors;
            var collectionConfiguration = new CollectionConfiguration(new CollectionConfigurationInfo() { Metrics = metrics }, out errors);

            var sample =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(collectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(3) },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            var accumulator = sample.CollectionConfigurationAccumulator.MetricAccumulators[Tuple.Create("Session1", "Metric1")];
            accumulator.Value.Push(1.0d);
            accumulator.Value.Push(2.0d);

            accumulator = sample.CollectionConfigurationAccumulator.MetricAccumulators[Tuple.Create("Session3", "Metric3")];
            accumulator.Value.Push(1.0d);
            accumulator.Value.Push(2.0d);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, "ETag1", out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            MetricPoint metric1 = this.samples.Single().Item3.Metrics.Single(m => m.SessionId == "Session1" && m.Name == "Metric1");
            MetricPoint metric2 = this.samples.Single().Item3.Metrics.Single(m => m.SessionId == "Session2" && m.Name == "Metric2");
            MetricPoint metric3 = this.samples.Single().Item3.Metrics.Single(m => m.SessionId == "Session3" && m.Name == "Metric3");

            Assert.AreEqual(1.5d, metric1.Value);
            Assert.AreEqual(2, metric1.Weight);
            Assert.AreEqual(1.5d, metric2.Value);
            Assert.AreEqual(2, metric2.Weight);
            Assert.AreEqual(3.0d, metric3.Value);
            Assert.AreEqual(2, metric3.Weight);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsInstanceNameToServiceWithPing()
        {
            // ARRANGE
            var instanceName = "this instance";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, instanceName, instanceName, instanceName, string.Empty, new Clock(), false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.pings.Count);
            Assert.AreEqual(instanceName, this.pings[0].Item1.InstanceName);
            Assert.AreEqual(instanceName, this.pings[0].Item3.Instance);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsInstanceNameToServiceWithSubmitSamples()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var instanceName = "this instance";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, instanceName, instanceName, instanceName, string.Empty, new Clock(), false);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.AreEqual(instanceName, this.samples[0].Item3.Instance);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsStreamIdToServiceWithPing()
        {
            // ARRANGE
            var streamId = "this stream";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, streamId, string.Empty, string.Empty, new Clock(), false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.pings.Count);
            Assert.AreEqual(streamId, this.pings[0].Item1.StreamId);
            Assert.AreEqual(streamId, this.pings[0].Item3.StreamId);
        }
        
        [TestMethod]
        public void QuickPulseServiceClientSubmitsStreamIdToServiceWithSubmitSamples()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var streamId = "this stream";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, streamId, string.Empty, string.Empty, new Clock(), false);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.AreEqual(streamId, this.samples[0].Item3.StreamId);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsMachineNameToServiceWithPing()
        {
            // ARRANGE
            var machineName = "this machine";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, machineName, string.Empty, new Clock(), false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.pings.Count);
            Assert.AreEqual(machineName, this.pings[0].Item1.MachineName);
            Assert.AreEqual(machineName, this.pings[0].Item3.MachineName);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsMachineNameToServiceWithSubmitSamples()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var machineName = "this machine";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, machineName, string.Empty, new Clock(), false);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.AreEqual(machineName, this.samples[0].Item3.MachineName);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsTransmissionTimeToServiceWithPing()
        {
            // ARRANGE
            var timeProvider = new ClockMock();
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, timeProvider, false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping(Guid.NewGuid().ToString(), timeProvider.UtcNow, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.pings.Count);
            Assert.AreEqual(timeProvider.UtcNow.Ticks, this.pings[0].Item1.TransmissionTime.Ticks);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsTransmissionTimeToServiceWithSubmitSamples()
        {
            // ARRANGE
            var timeProvider = new ClockMock();
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, timeProvider, false);
            var sample =
                new QuickPulseDataSample(
                    new QuickPulseDataAccumulator(this.emptyCollectionConfiguration)
                        {
                            StartTimestamp = timeProvider.UtcNow,
                            EndTimestamp = timeProvider.UtcNow.AddSeconds(1)
                        },
                    new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                    Enumerable.Empty<Tuple<string, int>>(),
                    false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.AreEqual(timeProvider.UtcNow.Ticks, this.samples[0].Item1.Ticks);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsVersionToServiceWithPing()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var version = "this version";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, version, new Clock(), false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.Ping("some ikey", now, string.Empty, out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.pingCount);
            Assert.AreEqual(version, this.lastVersion);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsVersionToServiceWithSubmitSamples()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var version = "this version";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, version, new Clock(), false);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.AreEqual(version, this.samples[0].Item3.Version);
            Assert.AreEqual(MonitoringDataPoint.CurrentInvariantVersion, this.samples[0].Item3.InvariantVersion);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsCollectionConfigurationETagToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);
            
            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, "ETag1", out configurationInfo, new string[0]);
            serviceClient.Ping(string.Empty, now, "ETag1", out configurationInfo);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual("ETag1", this.samples.Single().Item2);
            Assert.AreEqual("ETag1", this.pings.Single().Item2);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsCollectionConfigurationErrorsToServiceWithSubmitSamples()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);
            string[] collectionConfigurationErrors = { "Error1", "Error2" };

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, string.Empty, string.Empty, out configurationInfo, collectionConfigurationErrors);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(2, this.samples.Single().Item3.CollectionConfigurationErrors.Length);
            Assert.AreEqual("Error1", this.samples.Single().Item3.CollectionConfigurationErrors[0]);
            Assert.AreEqual("Error2", this.samples.Single().Item3.CollectionConfigurationErrors[1]);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsInstrumentationKeyToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var ikey = "some ikey";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), false);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, ikey, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.AreEqual(ikey, this.samples[0].Item3.InstrumentationKey);
        }
        
        [TestMethod]
        public void QuickPulseServiceClientSubmitsIsWebAppToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var ikey = "some ikey";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), true);
            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                Enumerable.Empty<Tuple<string, int>>(),
                false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, ikey, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.IsTrue(this.samples[0].Item3.IsWebApp);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsTopCpuProcessesToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var ikey = "some ikey";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), true);
            var cpuData = new[] { Tuple.Create("Process1", 1), Tuple.Create("Process2", 2) };

            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                cpuData,
                false);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, ikey, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.AreEqual(2, this.samples[0].Item3.TopCpuProcesses.Count());
            Assert.AreEqual("Process1", this.samples[0].Item3.TopCpuProcesses[0].ProcessName);
            Assert.AreEqual(1, this.samples[0].Item3.TopCpuProcesses[0].CpuPercentage);
            Assert.AreEqual("Process2", this.samples[0].Item3.TopCpuProcesses[1].ProcessName);
            Assert.AreEqual(2, this.samples[0].Item3.TopCpuProcesses[1].CpuPercentage);
        }

        [TestMethod]
        public void QuickPulseServiceClientSubmitsTopCpuProcessesAccessDeniedToService()
        {
            // ARRANGE
            var now = DateTimeOffset.UtcNow;
            var ikey = "some ikey";
            var serviceClient = new QuickPulseServiceClient(this.serviceEndpoint, string.Empty, string.Empty, string.Empty, string.Empty, new Clock(), true);

            var sample = new QuickPulseDataSample(
                new QuickPulseDataAccumulator(this.emptyCollectionConfiguration) { StartTimestamp = now, EndTimestamp = now.AddSeconds(1) },
                new Dictionary<string, Tuple<PerformanceCounterData, double>>(),
                new Tuple<string, int>[] { },
                true);

            // ACT
            CollectionConfigurationInfo configurationInfo;
            serviceClient.SubmitSamples(new[] { sample }, ikey, string.Empty, out configurationInfo, new string[0]);

            // ASSERT
            this.listener.Stop();

            Assert.AreEqual(1, this.samples.Count);
            Assert.IsTrue(this.samples[0].Item3.TopCpuDataAccessDenied);
        }

        public void Dispose()
        {
            ((IDisposable)this.listener).Dispose();
        }

        #region Helpers

        private void ProcessRequest(HttpListener listener)
        {
            var serializerDataPoint = new DataContractJsonSerializer(typeof(MonitoringDataPoint));
            var serializerDataPointArray = new DataContractJsonSerializer(typeof(MonitoringDataPoint[]));

            while (listener.IsListening)
            {
                HttpListenerContext context = listener.GetContext();

                var request = context.Request;
                if (request.Url.LocalPath == "/ping")
                {
                    this.pingCount++;

                    this.pingResponse(context.Response);

                    var dataPoint = (MonitoringDataPoint)serializerDataPoint.ReadObject(context.Request.InputStream);
                    var transmissionTime = long.Parse(context.Request.Headers[QuickPulseConstants.XMsQpsTransmissionTimeHeaderName], CultureInfo.InvariantCulture);
                    var instanceName = context.Request.Headers[QuickPulseConstants.XMsQpsInstanceNameHeaderName];
                    var machineName = context.Request.Headers[QuickPulseConstants.XMsQpsMachineNameHeaderName];
                    var streamId = context.Request.Headers[QuickPulseConstants.XMsQpsStreamIdHeaderName];
                    var collectionConfigurationETag = context.Request.Headers[QuickPulseConstants.XMsQpsConfigurationETagHeaderName];

                    this.pings.Add(
                        Tuple.Create(
                            new PingHeaders()
                                {
                                    TransmissionTime = new DateTimeOffset(transmissionTime, TimeSpan.Zero),
                                    InstanceName = instanceName,
                                    MachineName = machineName,
                                    StreamId = streamId
                                },
                            collectionConfigurationETag,
                            dataPoint));

                    this.lastPingTimestamp = dataPoint.Timestamp;
                    this.lastPingInstance = dataPoint.Instance;
                    this.lastVersion = dataPoint.Version;
                }
                else if (request.Url.LocalPath == "/post")
                {
                    this.submitCount++;

                    this.submitResponse(context.Response);

                    var dataPoints = serializerDataPointArray.ReadObject(context.Request.InputStream) as MonitoringDataPoint[];
                    var transmissionTime = long.Parse(context.Request.Headers[QuickPulseConstants.XMsQpsTransmissionTimeHeaderName], CultureInfo.InvariantCulture);
                    var collectionConfigurationETag = context.Request.Headers[QuickPulseConstants.XMsQpsConfigurationETagHeaderName];

                    this.samples.AddRange(dataPoints.Select(dp => Tuple.Create(new DateTimeOffset(transmissionTime, TimeSpan.Zero), collectionConfigurationETag, dp)));
                }

                if (!this.emulateTimeout)
                {
                    context.Response.Close();
                }
            }
        }

        #endregion

        private class PingHeaders
        {
            public DateTimeOffset TransmissionTime { get; set; }

            public string InstanceName { get; set; }

            public string MachineName { get; set; }

            public string StreamId { get; set; }
        }
    }
}