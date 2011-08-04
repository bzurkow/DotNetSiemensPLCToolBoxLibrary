﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DotNetSiemensPLCToolBoxLibrary.Communication;
using DotNetSimaticDatabaseProtokollerLibrary.Common;
using DotNetSimaticDatabaseProtokollerLibrary.Databases;
using DotNetSimaticDatabaseProtokollerLibrary.Databases.CSVFile;
using DotNetSimaticDatabaseProtokollerLibrary.Databases.Interfaces;
using DotNetSimaticDatabaseProtokollerLibrary.Databases.SQLite;
using DotNetSimaticDatabaseProtokollerLibrary.Protocolling.Trigger;
using DotNetSimaticDatabaseProtokollerLibrary.SettingsClasses.Connections;
using DotNetSimaticDatabaseProtokollerLibrary.SettingsClasses.Datasets;
using DotNetSimaticDatabaseProtokollerLibrary.SettingsClasses.Storage;

namespace DotNetSimaticDatabaseProtokollerLibrary.Protocolling
{
    public class ProtokollerInstance : IDisposable
    {
        private ProtokollerConfiguration akConfig;
        private Dictionary<ConnectionConfig, Object> ConnectionList = new Dictionary<ConnectionConfig, object>();
        private Dictionary<DatasetConfig, IDBInterface> DatabaseInterfaces = new Dictionary<DatasetConfig, IDBInterface>();

        private List<IDisposable> myDisposables = new List<IDisposable>();
        private Thread myReEstablishConnectionsThread;
        
        public ProtokollerInstance(ProtokollerConfiguration akConfig)
        {
            this.akConfig = akConfig;           
        }

        public void Start()
        {
            Logging.LogText("Protokoller gestartet", Logging.LogLevel.Information);
            EstablishConnections();
            OpenStoragesAndCreateTriggers(true);            
        }

        public void StartTestMode()
        {
            EstablishConnections();
            OpenStoragesAndCreateTriggers(false);
        }

        private void ReEstablishConnectionsThreadProc()
        {
            try
            {
                while (true)
                {
                    foreach (ConnectionConfig connectionConfig in akConfig.Connections)
                    {
                        if (ConnectionList.ContainsKey(connectionConfig))
                        {
                            PLCConnection plcConn = ConnectionList[connectionConfig] as PLCConnection;
                            TCPFunctions tcpipFunc = ConnectionList[connectionConfig] as TCPFunctions;
                            if (plcConn != null && !plcConn.Connected)
                            {
                                try
                                {
                                    plcConn.Connect();
                                    Logging.LogText("Connection: " + connectionConfig.Name + " connected", Logging.LogLevel.Information);
                                }
                                catch (ThreadAbortException ex)
                                {
                                    throw ex;
                                }
                                catch (Exception ex)
                                {
                                    Logging.LogText("Connection: " + connectionConfig.Name + " Error: " + ex.Message, Logging.LogLevel.Warning);
                                }
                            }
                        }
                    }
                    Thread.Sleep(500);
                }
            }
            catch (ThreadAbortException)
            {
            }
        }

        private void EstablishConnections()
        {
                       
            foreach (ConnectionConfig connectionConfig in akConfig.Connections)
            {
                LibNoDaveConfig plcConnConf = connectionConfig as LibNoDaveConfig;
                TCPIPConfig tcpipConnConf = connectionConfig as TCPIPConfig;

                if (plcConnConf != null)
                {
                    PLCConnection tmpConn = new PLCConnection(plcConnConf.Configuration);
                    try
                    {
                        tmpConn.Connect();
                    }
                    catch (Exception ex)
                    {
                        Logging.LogText("Connection: " + connectionConfig.Name + " Error:" + ex.Message, Logging.LogLevel.Warning);
                    }

                    ConnectionList.Add(connectionConfig, tmpConn);
                }
                else if (tcpipConnConf != null)
                {
                    //todo: legth of tcp conn
                    TCPFunctions tmpConn = new TCPFunctions(new SynchronizationContext(), tcpipConnConf.IPasIPAddres, tcpipConnConf.Port, !tcpipConnConf.PassiveConnection, 0);
                    
                    tmpConn.Connect();
                    
                    ConnectionList.Add(connectionConfig, tmpConn);
                }
            }            

            myReEstablishConnectionsThread = new Thread(new ThreadStart(ReEstablishConnectionsThreadProc)) { Name = "EstablishConnectionsThreadProc" };
            myReEstablishConnectionsThread.Start();                        

        }

        public void TestTriggers(DatasetConfig testDataset)
        {
            if (testDataset.Trigger == DatasetTriggerType.Tags_Handshake_Trigger)
            {
                EstablishConnections();

                PLCConnection conn = ConnectionList[testDataset.TriggerConnection] as PLCConnection;
                if (conn != null)
                {
                    conn.ReadValue(testDataset.TriggerReadBit);
                    conn.ReadValue(testDataset.TriggerQuittBit);
                }
            }
            
        }


        public void TestDataRead(DatasetConfig testDataset)
        {
            ReadData.ReadDataFromPLCs(testDataset.DatasetConfigRows, ConnectionList);
        }

        public void TestDataReadWrite(DatasetConfig testDataset)
        {
            DatabaseInterfaces[testDataset].Write(ReadData.ReadDataFromPLCs(testDataset.DatasetConfigRows, ConnectionList));
        }

        private void OpenStoragesAndCreateTriggers(bool CreateTriggers)
        {
            foreach (DatasetConfig datasetConfig in akConfig.Datasets)
            {
                IDBInterface akDBInterface = null;

                akDBInterface = StorageHelper.GetStorage(datasetConfig);
                
                DatabaseInterfaces.Add(datasetConfig, akDBInterface);

                akDBInterface.Connect_To_Database(datasetConfig.Storage);
                akDBInterface.CreateOrModify_TablesAndFields(datasetConfig.Name, datasetConfig);

                if (CreateTriggers)
                    if (datasetConfig.Trigger == DatasetTriggerType.Tags_Handshake_Trigger)
                    {
                        PLCTagTriggerThread tmpTrigger = new PLCTagTriggerThread(akDBInterface, datasetConfig, ConnectionList);
                        tmpTrigger.StartTrigger();
                        myDisposables.Add(tmpTrigger);
                    }
                    else if (datasetConfig.Trigger == DatasetTriggerType.Time_Trigger)
                    {
                        TimeTriggerThread tmpTrigger = new TimeTriggerThread(akDBInterface, datasetConfig, ConnectionList);
                        tmpTrigger.StartTrigger();
                        myDisposables.Add(tmpTrigger);
                    }
                    else if (datasetConfig.Trigger == DatasetTriggerType.Triggered_By_Incoming_Data_On_A_TCPIP_Connection)
                    {

                    }
            }
        }        

        public void Dispose()
        {
            Logging.LogText("Protokoller gestopt", Logging.LogLevel.Information);
            if (myReEstablishConnectionsThread != null)
                myReEstablishConnectionsThread.Abort();

            foreach (var disposable in myDisposables)
            {
                disposable.Dispose();
            }

            foreach (object conn in ConnectionList.Values)
            {
                if (conn is PLCConnection)
                    ((PLCConnection)conn).Dispose();
                else if (conn is TCPFunctions)
                    ((TCPFunctions)conn).Dispose();
            }

            foreach (IDBInterface dbInterface in DatabaseInterfaces.Values)
            {
                dbInterface.Close();
            }
        }
    }
}