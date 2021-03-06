﻿// <copyright file="SqlEventSourceListener.netfx.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
#if NETFRAMEWORK
using System;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.Dependencies.Implementation
{
    /// <summary>
    /// .NET Framework SqlClient doesn't emit DiagnosticSource events.
    /// We hook into its EventSource if it is available:
    /// See: <a href="https://github.com/microsoft/referencesource/blob/3b1eaf5203992df69de44c783a3eda37d3d4cd10/System.Data/System/Data/Common/SqlEventSource.cs#L29">reference source</a>.
    /// </summary>
    internal class SqlEventSourceListener : EventListener
    {
        internal const string ActivitySourceName = "System.Data.SqlClient";
        internal const string ActivityName = ActivitySourceName + ".Execute";
        internal const string AdoNetEventSourceName = "Microsoft-AdoNet-SystemData";
        internal const int BeginExecuteEventId = 1;
        internal const int EndExecuteEventId = 2;

        private static readonly Version Version = typeof(SqlEventSourceListener).Assembly.GetName().Version;
        private static readonly ActivitySource SqlClientActivitySource = new ActivitySource(ActivitySourceName, Version.ToString());

        private readonly SqlClientInstrumentationOptions options;
        private EventSource eventSource;

        public SqlEventSourceListener(SqlClientInstrumentationOptions options = null)
        {
            this.options = options ?? new SqlClientInstrumentationOptions();
        }

        public override void Dispose()
        {
            if (this.eventSource != null)
            {
                this.DisableEvents(this.eventSource);
            }

            base.Dispose();
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource?.Name.StartsWith(AdoNetEventSourceName) == true)
            {
                this.eventSource = eventSource;
                this.EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)1);
            }

            base.OnEventSourceCreated(eventSource);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            try
            {
                if (eventData.EventId == BeginExecuteEventId)
                {
                    this.OnBeginExecute(eventData);
                }
                else if (eventData.EventId == EndExecuteEventId)
                {
                    this.OnEndExecute(eventData);
                }
            }
            catch (Exception exc)
            {
                InstrumentationEventSource.Log.UnknownErrorProcessingEvent(nameof(SqlEventSourceListener), nameof(this.OnEventWritten), exc);
            }
        }

        private void OnBeginExecute(EventWrittenEventArgs eventData)
        {
            /*
               Expected payload:
                [0] -> ObjectId
                [1] -> DataSource
                [2] -> Database
                [3] -> CommandText ([3] = CommandType == CommandType.StoredProcedure ? CommandText : string.Empty)
             */

            if ((eventData?.Payload?.Count ?? 0) < 4)
            {
                InstrumentationEventSource.Log.InvalidPayload(nameof(SqlEventSourceListener), nameof(this.OnBeginExecute));
                return;
            }

            var activity = SqlClientActivitySource.StartActivity(ActivityName, ActivityKind.Client);
            if (activity == null)
            {
                return;
            }

            string databaseName = (string)eventData.Payload[2];

            activity.DisplayName = databaseName;

            if (activity.IsAllDataRequested)
            {
                activity.AddTag(SpanAttributeConstants.ComponentKey, "sql");

                activity.AddTag(SpanAttributeConstants.DatabaseSystemKey, SqlClientDiagnosticListener.MicrosoftSqlServerDatabaseSystemName);
                activity.AddTag(SpanAttributeConstants.DatabaseNameKey, databaseName);

                this.options.AddConnectionLevelDetailsToActivity((string)eventData.Payload[1], activity);

                string commandText = (string)eventData.Payload[3];
                if (string.IsNullOrEmpty(commandText))
                {
                    activity.AddTag(SpanAttributeConstants.DatabaseStatementTypeKey, nameof(CommandType.Text));
                }
                else
                {
                    activity.AddTag(SpanAttributeConstants.DatabaseStatementTypeKey, nameof(CommandType.StoredProcedure));
                    if (this.options.CaptureStoredProcedureCommandName)
                    {
                        activity.AddTag(SpanAttributeConstants.DatabaseStatementKey, commandText);
                    }
                }
            }
        }

        private void OnEndExecute(EventWrittenEventArgs eventData)
        {
            /*
               Expected payload:
                [0] -> ObjectId
                [1] -> CompositeState bitmask (0b001 -> successFlag, 0b010 -> isSqlExceptionFlag , 0b100 -> synchronousFlag)
                [2] -> SqlExceptionNumber
             */

            if ((eventData?.Payload?.Count ?? 0) < 3)
            {
                InstrumentationEventSource.Log.InvalidPayload(nameof(SqlEventSourceListener), nameof(this.OnEndExecute));
                return;
            }

            var activity = Activity.Current;
            if (activity?.Source != SqlClientActivitySource)
            {
                return;
            }

            try
            {
                if (activity.IsAllDataRequested)
                {
                    int compositeState = (int)eventData.Payload[1];
                    if ((compositeState & 0b001) == 0b001)
                    {
                        activity.AddTag(SpanAttributeConstants.StatusCodeKey, SpanHelper.GetCachedCanonicalCodeString(StatusCanonicalCode.Ok));
                    }
                    else
                    {
                        activity.AddTag(SpanAttributeConstants.StatusCodeKey, SpanHelper.GetCachedCanonicalCodeString(StatusCanonicalCode.Unknown));
                        if ((compositeState & 0b010) == 0b010)
                        {
                            activity.AddTag(SpanAttributeConstants.StatusDescriptionKey, $"SqlExceptionNumber {eventData.Payload[2]} thrown.");
                        }
                        else
                        {
                            activity.AddTag(SpanAttributeConstants.StatusDescriptionKey, $"Unknown Sql failure.");
                        }
                    }
                }
            }
            finally
            {
                activity.Stop();
            }
        }
    }
}
#endif
