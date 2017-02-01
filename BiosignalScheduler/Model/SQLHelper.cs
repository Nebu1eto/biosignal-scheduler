﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Castle.Core.Logging;

namespace BiosignalScheduler.Model
{
    internal class SqlHelper
    {
        private static SqlHelper _instance;
        private Connection _innerConn;
        private readonly ConsoleLogger _logger;
        private readonly List<DatabaseModel.MappingTable> _mappingTables;
        private readonly List<DatabaseModel.PatientIdMap> _patientIdMaps;

        public static SqlHelper Instance(Connection conn)
        {
            if (_instance == null) _instance = new SqlHelper(conn);
            else _instance._innerConn = conn;
            return _instance;
        }

        private SqlHelper(Connection conn)
        {
            _innerConn = conn;
            _logger = new ConsoleLogger("SqlHelper", LoggerLevel.Error);
            _mappingTables = Await(GetMappingTablesAsync());
            _patientIdMaps = Await(GetPatientIdMapsAsync());
        }

        private static T Await<T>(Task<T> task)
        {
            task.Wait();
            return task.Result;
        }

        private static string EncryptSha256(string origin)
        {
            using (var alg = SHA512.Create())
                return BitConverter.ToString(alg.ComputeHash(Encoding.UTF8.GetBytes(origin)));
        }

        public static string DateTimeToString(DateTime dt, string format = "{0:yyyyMMddHHmmss}")
            => string.Format(format, dt);

        public string GetAnonymousId(string patientId)
        {
            if (_patientIdMaps.Any(item => item.PatientId.Equals(patientId)))
                return _patientIdMaps.Find(item => item.PatientId.Equals(patientId)).AnonymousId;
            else
            {
                var anonymousId = EncryptSha256(patientId).Substring(0, 20);
                AddPatientIdMap(patientId, anonymousId).Wait();
                return anonymousId;
            }
        }

        public string GetMetricId(string origin)
        {
            return (_mappingTables.Any(obj => obj.Observation.Equals(origin)))
                ? _mappingTables.FindLast(obj => obj.Observation.Equals(origin)).Observation
                : origin;
        }

        private async Task AddPatientIdMap(string patientId, string anonymousId)
        {
            const string sql = @"INSERT INTO patientId_mapping (patient_id, anonymous_id) 
                VALUES (@patientId, @anonymousId)";

            using (var conn = new SqlConnection(_innerConn.ToString()))
            using (var command = new SqlCommand(sql, conn))
            {
                try
                {
                    command.Parameters.AddWithValue("@patientId", patientId);
                    command.Parameters.AddWithValue("@anonymousId", anonymousId);

                    await conn.OpenAsync();
                    command.CommandType = CommandType.Text;
                    await command.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception on Evaluation SQL: {sql}", ex);
                }
            }

            _patientIdMaps.Add(new DatabaseModel.PatientIdMap
            {
                PatientId = patientId,
                AnonymousId = anonymousId
            });
        }

        public async Task InsertWaveformValueAsync(MqModel model,
            string filePath, DateTime startTime, DateTime endTime)
        {
            if (model.IsNumeric)
                throw new ArgumentException();

            const string sql = @"INSERT INTO waveform_info (patient_id, starttime, endtime, wavetype, filepath)
                                VALUES (@patientId, @startTime, @endTime, @waveType, @filePath);";

            using (var conn = new SqlConnection(_innerConn.ToString()))
            using (var command = new SqlCommand(sql, conn))
            {
                try
                {
                    command.Parameters.AddWithValue("@patientId", GetAnonymousId(model.PatientId));
                    command.Parameters.AddWithValue("@startTime", DateTimeToString(startTime));
                    command.Parameters.AddWithValue("@endTime", DateTimeToString(endTime));
                    command.Parameters.AddWithValue("@waveType", GetMetricId(model.Type));
                    command.Parameters.AddWithValue("@filePath", filePath);

                    await conn.OpenAsync();
                    command.CommandType = CommandType.Text;
                    await command.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception on Evaluation SQL: {sql}", ex);
                }
            }
        }

        public async Task InsertNumericValueAsync(MqModel model)
        {
            if (!model.IsNumeric)
                throw new ArgumentException();

            const string sql = @"
                INSERT INTO patient_info 
                (patient_id, timestamp, observation, observation_value, observation_value_unit, ip_address)
                VALUES (@patientId, @timestamp, @observation, @observationValue, @observationValueUnit, @ipAddr);
            ";

            using (var conn = new SqlConnection(_innerConn.ToString()))
            using (var command = new SqlCommand(sql, conn))
            {
                try
                {
                    command.Parameters.AddWithValue("@patientId", GetAnonymousId(model.PatientId));
                    command.Parameters.AddWithValue("@timestamp", DateTimeToString(model.Timestamp));
                    command.Parameters.AddWithValue("@observation", GetMetricId(model.Type));
                    command.Parameters.AddWithValue("@observationValue", model.GetValue());
                    command.Parameters.AddWithValue("@observationValueUnit", model.Unit);
                    command.Parameters.AddWithValue("@ipAddr", $"{model.Host}:{model.Port}");

                    await conn.OpenAsync();
                    command.CommandType = CommandType.Text;
                    await command.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception on Evaluation SQL: {sql}", ex);
                }
            }
        }

        public async Task<List<DatabaseModel.PatientIdMap>> GetPatientIdMapsAsync()
        {
            const string sql = "SELECT * FROM patient_map";
            var table = new List<DatabaseModel.PatientIdMap>();

            using (var conn = new SqlConnection(_innerConn.ToString()))
            using (var command = new SqlCommand(sql, conn))
            {
                try
                {
                    await conn.OpenAsync();
                    var reader = await command.ExecuteReaderAsync();

                    var ndxPatientId = reader.GetOrdinal("patient_id");
                    var ndxAnonymousId = reader.GetOrdinal("anonymous_id");

                    while (await reader.ReadAsync())
                    {
                        var patientId = await reader.GetFieldValueAsync<string>(ndxPatientId);
                        var anonymousId = await reader.GetFieldValueAsync<string>(ndxAnonymousId);
                        table.Add(new DatabaseModel.PatientIdMap
                        {
                            PatientId = patientId,
                            AnonymousId = anonymousId
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception on Evaluation SQL: {sql}", ex);
                }
            }

            return table;
        }

        public async Task<List<DatabaseModel.MappingTable>> GetMappingTablesAsync()
        {
            const string sql = "SELECT * FROM mapping_table";
            var table = new List<DatabaseModel.MappingTable>();

            using (var conn = new SqlConnection(_innerConn.ToString()))
            using (var command = new SqlCommand(sql, conn))
            {
                try
                {
                    await conn.OpenAsync();
                    var reader = await command.ExecuteReaderAsync();

                    var ndxObservation = reader.GetOrdinal("observation");
                    var ndxWaveName = reader.GetOrdinal("wave_name");
                    var ndxObservationType = reader.GetOrdinal("observation_type");

                    while (await reader.ReadAsync())
                    {
                        var observation = await reader.GetFieldValueAsync<string>(ndxObservation);
                        var waveName = await reader.GetFieldValueAsync<string>(ndxWaveName);
                        var observationType = await reader.GetFieldValueAsync<long>(ndxObservationType);
                        table.Add(new DatabaseModel.MappingTable
                        {
                            Observation = observation, 
                            WaveName = waveName,
                            ObservationType = observationType
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception on Evaluation SQL: {sql}", ex);
                }
            }

            return table;
        }
        
        public async Task ExecuteAsync(string sql)
        {
            using (var conn = new SqlConnection(_innerConn.ToString()))
            using (var command = new SqlCommand(sql, conn))
            {
                try
                {
                    await conn.OpenAsync();
                    command.CommandType = CommandType.Text;
                    await command.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception on Evaluation SQL: {sql}", ex);
                }
            }
        }

        [Serializable]
        public class Connection
        {
            public string Server { get; set; }
            public string UserId { get; set; }
            public string Password { get; set; }
            public string Database { get; set; }

            public override string ToString()
            {
                return $"server={Server}; uid={UserId}; password={Password} database={Database}";
            }
        }
    }
}