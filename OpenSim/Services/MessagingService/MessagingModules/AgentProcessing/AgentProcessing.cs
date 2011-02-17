﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Console;
using Aurora.Simulation.Base;
using OpenSim.Services.Interfaces;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using log4net;

namespace OpenSim.Services.MessagingService.MessagingModules.GridWideMessage
{
    public class AgentProcessing : IService
    {
        #region Declares

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        protected IRegistryCore m_registry;

        #endregion

        #region IService Members

        public void Initialize(IConfigSource config, IRegistryCore registry)
        {
        }

        public void Start(IConfigSource config, IRegistryCore registry)
        {
            m_registry = registry;
            //Also look for incoming messages to display
            registry.RequestModuleInterface<IAsyncMessageRecievedService>().OnMessageReceived += OnMessageReceived;
        }

        #endregion

        #region Message Received

        protected OSDMap OnMessageReceived(OSDMap message)
        {
            if (!message.ContainsKey("Method"))
                return null;

            UUID AgentID = message["AgentID"].AsUUID();
            ulong requestingRegion = message["RequestingRegion"].AsULong();
            IClientCapsService clientCaps = m_registry.RequestModuleInterface<ICapsService>().GetClientCapsService(AgentID);
            IRegionClientCapsService regionCaps = clientCaps.GetCapsService(requestingRegion);
            
            if(message["Method"] == "EnableChildAgents")
            {
                //Some notes on this message:
                // 1) This is a region > CapsService message ONLY, this should never be sent to the client!
                // 2) This just enables child agents in the regions given, as the region cannot do it,
                //       as regions do not have the ability to know what Cap Urls other regions have.
                // 3) We could do more checking here, but we don't really 'have' to at this point.
                //       If the sim was able to get it past the password checks and everything,
                //       it should be able to add the neighbors here. We could do the neighbor finding here
                //       as well, but it's not necessary at this time.
                OSDMap body = ((OSDMap)message["Message"]);

                //Parse the OSDMap
                int DrawDistance = body["DrawDistance"].AsInteger();

                AgentCircuitData circuitData = new AgentCircuitData();
                circuitData.UnpackAgentCircuitData((OSDMap)body["Circuit"]);

                //Now do the creation
                //Don't send it to the client at all, so return here
                EnableChildAgents(AgentID, requestingRegion, DrawDistance, circuitData);
            }
            else if (message["Message"] == "DisableSimulator")
            {
                //Let this pass through, after the next event queue pass we can remove it
                //m_service.ClientCaps.RemoveCAPS(m_service.RegionHandle);
                if (!regionCaps.Disabled)
                {
                    regionCaps.Disabled = true;
                    OSDMap body = ((OSDMap)message["Message"]);
                    //See whether this needs sent to the client or not
                    if (!body["KillClient"].AsBoolean())
                    {
                        //This is very risky... but otherwise the user doesn't get cleaned up...
                        clientCaps.RemoveCAPS(requestingRegion);
                    }
                }
            }
            else if (message["Message"] == "ArrivedAtDestination")
            {
                //Recieved a callback
                clientCaps.CallbackHasCome = true;
                regionCaps.Disabled = false;
            }
            else if (message["Message"] == "CancelTeleport")
            {
                //The user has requested to cancel the teleport, stop them.
                clientCaps.RequestToCancelTeleport = true;
                regionCaps.Disabled = false;
            }
            else if (message["Message"] == "SendChildAgentUpdate")
            {
                OSDMap body = ((OSDMap)message["Message"]);

                AgentPosition pos = new AgentPosition();
                pos.Unpack((OSDMap)body["AgentPos"]);
                UUID region = body["Region"].AsUUID();

                SendChildAgentUpdate(pos, region);
                regionCaps.Disabled = false;
            }
            else if (message["Message"] == "TeleportAgent")
            {
                OSDMap body = ((OSDMap)message["Message"]);

                GridRegion destination = new GridRegion();
                destination.FromOSD((OSDMap)body["Region"]);

                uint TeleportFlags = body["TeleportFlags"].AsUInteger();
                int DrawDistance = body["DrawDistance"].AsInteger();

                AgentCircuitData Circuit = new AgentCircuitData();
                Circuit.UnpackAgentCircuitData((OSDMap)body["Circuit"]);

                AgentData AgentData = new AgentData();
                AgentData.Unpack((OSDMap)body["AgentData"]);
                regionCaps.Disabled = false;

                TeleportAgent(destination, TeleportFlags, DrawDistance, Circuit, AgentData, AgentID, requestingRegion);
            }
            else if (message["Message"] == "CrossAgent")
            {
                //This is a simulator message that tells us to cross the agent
                OSDMap body = ((OSDMap)message["Message"]);

                Vector3 pos = body["Pos"].AsVector3();
                Vector3 Vel = body["Vel"].AsVector3();
                GridRegion Region = new GridRegion();
                Region.FromOSD((OSDMap)body["Region"]);
                AgentCircuitData Circuit = new AgentCircuitData();
                Circuit.UnpackAgentCircuitData((OSDMap)body["Circuit"]);
                AgentData AgentData = new AgentData();
                AgentData.Unpack((OSDMap)body["AgentData"]);
                regionCaps.Disabled = false;

                CrossAgent(Region, pos, Vel, Circuit, AgentData, AgentID, requestingRegion);
            }
            return null;
        }

        #region EnableChildAgents

        public bool EnableChildAgents(UUID AgentID, ulong requestingRegion, int DrawDistance, AgentCircuitData circuit)
        {
            int count = 0;
            bool informed = true;
            INeighborService neighborService = m_registry.RequestModuleInterface<INeighborService>();
            if (neighborService != null)
            {
                uint x, y;
                Utils.LongToUInts(requestingRegion, out x, out y);
                GridRegion ourRegion = m_registry.RequestModuleInterface<IGridService>().GetRegionByPosition(UUID.Zero, (int)x, (int)y);
                if (ourRegion == null)
                {
                    m_log.Info("[EQMService]: Failed to inform neighbors about new agent, could not find our region. ");
                    return false;
                }
                List<GridRegion> neighbors = neighborService.GetNeighbors(ourRegion, DrawDistance);

                foreach (GridRegion neighbor in neighbors)
                {
                    //m_log.WarnFormat("--> Going to send child agent to {0}, new agent {1}", neighbour.RegionName, newAgent);

                    if (neighbor.RegionHandle != requestingRegion)
                    {
                        if (!InformClientOfNeighbor(AgentID, requestingRegion, circuit.Copy(), neighbor,
                            (uint)TeleportFlags.Default, null))
                            informed = false;
                    }
                    count++;
                }
            }
            return informed;
        }

        /// <summary>
        /// Async component for informing client of which neighbors exist
        /// </summary>
        /// <remarks>
        /// This needs to run asynchronously, as a network timeout may block the thread for a long while
        /// </remarks>
        /// <param name="remoteClient"></param>
        /// <param name="a"></param>
        /// <param name="regionHandle"></param>
        /// <param name="endPoint"></param>
        private bool InformClientOfNeighbor(UUID AgentID, ulong requestingRegion, AgentCircuitData circuitData, GridRegion neighbor,
            uint TeleportFlags, AgentData agentData)
        {
            m_log.Info("[EventQueueService]: Starting to inform client about neighbor " + neighbor.RegionName);

            //Notes on this method
            // 1) the SimulationService.CreateAgent MUST have a fixed CapsUrl for the region, so we have to create (if needed)
            //       a new Caps handler for it.
            // 2) Then we can call the methods (EnableSimulator and EstatablishAgentComm) to tell the client the new Urls
            // 3) This allows us to make the Caps on the grid server without telling any other regions about what the
            //       Urls are.

            string reason = String.Empty;
            ISimulationService SimulationService = m_registry.RequestModuleInterface<ISimulationService>();
            if (SimulationService != null)
            {
                //Make sure that we have a URL for the Caps on the grid server and one for the sim
                string newSeedCap = CapsUtil.GetCapsSeedPath(CapsUtil.GetRandomCapsObjectPath());
                //Leave this blank so that we can check below so that we use the same Url if the client has already been to that region
                string SimSeedCap = "";

                ICapsService capsService = m_registry.RequestModuleInterface<ICapsService>();

                IClientCapsService clientCaps = capsService.GetClientCapsService(AgentID);

                IRegionClientCapsService oldRegionService = clientCaps.GetCapsService(neighbor.RegionHandle);
                bool newAgent = oldRegionService == null;
                IRegionClientCapsService otherRegionService = clientCaps.GetOrCreateCapsService(neighbor.RegionHandle, newSeedCap, SimSeedCap);

                //ONLY UPDATE THE SIM SEED HERE
                //DO NOT PASS THE newSeedCap FROM ABOVE AS IT WILL BREAK THIS CODE
                // AS THE CLIENT EXPECTS THE SAME CAPS SEED IF IT HAS BEEN TO THE REGION BEFORE
                // AND FORCE UPDATING IT HERE WILL BREAK IT.
                string CapsBase = CapsUtil.GetRandomCapsObjectPath();
                if (newAgent)
                {
                    //Update 21-1-11 (Revolution) This is still very much needed for standalone mode
                    //The idea behind this is that this is the FIRST setup in standalone mode, so setting it to
                    //   this sets up the first initial URL, as if it was set as "", it would set it up wrong 
                    //   initially and all will go wrong from there
                    //Build the full URL
                    SimSeedCap
                        = neighbor.ServerURI
                      + CapsUtil.GetCapsSeedPath(CapsBase);
                    //Add the new Seed for this region
                }
                else if (!oldRegionService.Disabled)
                {
                    //Note: if the agent is already there, send an agent update then
                    if (agentData != null)
                        return SimulationService.UpdateAgent(neighbor, agentData);
                    return true;
                }
                //Fix the AgentCircuitData with the new CapsUrl
                circuitData.CapsPath = CapsBase;
                //Add the password too
                circuitData.OtherInformation["CapsPassword"] = otherRegionService.Password;

                //Offset the child avs position
                uint x, y;
                Utils.LongToUInts(requestingRegion, out x, out y);

                bool regionAccepted = SimulationService.CreateAgent(neighbor, circuitData,
                        TeleportFlags, agentData, out reason);
                if (regionAccepted)
                {
                    //If the region accepted us, we should get a CAPS url back as the reason, if not, its not updated or not an Aurora region, so don't touch it.
                    if (reason != "")
                    {
                        OSDMap responseMap = (OSDMap)OSDParser.DeserializeJson(reason);
                        SimSeedCap = responseMap["CapsUrl"].AsString();
                    }
                    //ONLY UPDATE THE SIM SEED HERE
                    //DO NOT PASS THE newSeedCap FROM ABOVE AS IT WILL BREAK THIS CODE
                    // AS THE CLIENT EXPECTS THE SAME CAPS SEED IF IT HAS BEEN TO THE REGION BEFORE
                    // AND FORCE UPDATING IT HERE WILL BREAK IT.
                    otherRegionService.AddSEEDCap("", SimSeedCap, otherRegionService.Password);
                    if (newAgent)
                    {
                        //We 'could' call Enqueue directly... but its better to just let it go and do it this way
                        IEventQueueService EQService = m_registry.RequestModuleInterface<IEventQueueService>();

                        EQService.EnableSimulator(neighbor.RegionHandle,
                            neighbor.ExternalEndPoint.Address.GetAddressBytes(),
                            neighbor.ExternalEndPoint.Port, AgentID,
                            neighbor.RegionSizeX, neighbor.RegionSizeY, requestingRegion);

                        // ES makes the client send a UseCircuitCode message to the destination, 
                        // which triggers a bunch of things there.
                        // So let's wait
                        Thread.Sleep(300);
                        EQService.EstablishAgentCommunication(AgentID, neighbor.RegionHandle,
                            neighbor.ExternalEndPoint.Address.GetAddressBytes(),
                            neighbor.ExternalEndPoint.Port, otherRegionService.UrlToInform, neighbor.RegionSizeX,
                            neighbor.RegionSizeY,
                            requestingRegion);

                        m_log.Info("[EventQueueService]: Completed inform client about neighbor " + neighbor.RegionName);
                    }
                }
                else
                {
                    m_log.Error("[EventQueueService]: Failed to inform client about neighbor " + neighbor.RegionName + ", reason: " + reason);
                    return false;
                }
                return true;
            }
            m_log.Error("[EventQueueService]: Failed to inform client about neighbor " + neighbor.RegionName + ", reason: SimulationService does not exist!");
            return false;
        }

        #endregion

        #region Teleporting

        protected bool TeleportAgent(GridRegion destination, uint TeleportFlags, int DrawDistance,
            AgentCircuitData circuit, AgentData agentData, UUID AgentID, ulong requestingRegion)
        {
            bool result = false;

            ISimulationService SimulationService = m_registry.RequestModuleInterface<ISimulationService>();
            if (SimulationService != null)
            {
                //Set the user in transit so that we block duplicate tps and reset any cancelations
                if (!SetUserInTransit(AgentID))
                    return false;

                //Note: we have to pull the new grid region info as the one from the region cannot be trusted
                IGridService GridService = m_registry.RequestModuleInterface<IGridService>();
                if (GridService != null)
                {
                    destination = GridService.GetRegionByUUID(UUID.Zero, destination.RegionID);
                    //Inform the client of the neighbor if needed
                    if (!InformClientOfNeighbor(AgentID, requestingRegion, circuit, destination, TeleportFlags,
                        agentData))
                        return false;
                }
                else
                    return false;

                uint x, y;
                Utils.LongToUInts(requestingRegion, out x, out y);
                IEventQueueService EQService = m_registry.RequestModuleInterface<IEventQueueService>();

                IClientCapsService clientCaps = m_registry.RequestModuleInterface<ICapsService>().GetClientCapsService(AgentID);
                IRegionClientCapsService regionCaps = clientCaps.GetCapsService(requestingRegion);

                IRegionClientCapsService otherRegion = clientCaps.GetCapsService(destination.RegionHandle);

                EQService.TeleportFinishEvent(destination.RegionHandle, destination.Access, destination.ExternalEndPoint, otherRegion.CapsUrl,
                                           4, AgentID, TeleportFlags,
                                           destination.RegionSizeX, destination.RegionSizeY,
                                           requestingRegion);

                // TeleportFinish makes the client send CompleteMovementIntoRegion (at the destination), which
                // trigers a whole shebang of things there, including MakeRoot. So let's wait for confirmation
                // that the client contacted the destination before we send the attachments and close things here.

                INeighborService service = m_registry.RequestModuleInterface<INeighborService>();
                if (service != null)
                {
                    bool callWasCanceled = false;
                    result = WaitForCallback(AgentID, out callWasCanceled);
                    if (!result)
                    {
                        //It says it failed, lets call the sim and check
                        IAgentData data = null;
                        if (!SimulationService.RetrieveAgent(destination, AgentID, out data))
                        {
                            if (!callWasCanceled)
                            {
                                m_log.Warn("[EntityTransferModule]: Callback never came for teleporting agent " +
                                    AgentID + ". Resetting.");
                            }
                            //Close the agent at the place we just created if it isn't a neighbor
                            if (service.IsOutsideView((int)x, destination.RegionLocX,
                                (int)y, destination.RegionLocY))
                                SimulationService.CloseAgent(destination, AgentID);
                        }
                        else
                        {
                            //Fix the root agent status
                            otherRegion.RootAgent = true;
                            regionCaps.RootAgent = false;

                            //Ok... the agent exists... so lets assume that it worked?
                            service.CloseNeighborAgents(destination.RegionLocX, destination.RegionLocY, AgentID, requestingRegion);
                            //Make sure to set the result correctly as well
                            result = true;
                        }
                    }
                    else
                    {
                        //Fix the root agent status
                        otherRegion.RootAgent = true;
                        regionCaps.RootAgent = false;

                        // Next, let's close the child agent connections that are too far away.
                        service.CloseNeighborAgents(destination.RegionLocX, destination.RegionLocY, AgentID, requestingRegion);
                    }
                }

                //All done
                ResetFromTransit(AgentID);
            }

            return result;
        }

        protected void ResetFromTransit(UUID AgentID)
        {
            IClientCapsService clientCaps = m_registry.RequestModuleInterface<ICapsService>().GetClientCapsService(AgentID);

            clientCaps.InTeleport = false;
            clientCaps.RequestToCancelTeleport = false;
            clientCaps.CallbackHasCome = false;
        }

        protected bool SetUserInTransit(UUID AgentID)
        {
            IClientCapsService clientCaps = m_registry.RequestModuleInterface<ICapsService>().GetClientCapsService(AgentID);
            
            if (clientCaps.InTeleport)
            {
                m_log.Warn("[EventQueueService]: Got a request to teleport during another teleport for agent " + AgentID + "!");
                return false; //What??? Stop here and don't go forward
            }

            clientCaps.InTeleport = true;
            clientCaps.RequestToCancelTeleport = false;
            clientCaps.CallbackHasCome = false;
            return true;
        }

        #region Callbacks

        protected bool WaitForCallback(UUID AgentID, out bool callWasCanceled)
        {
            IClientCapsService clientCaps = m_registry.RequestModuleInterface<ICapsService>().GetClientCapsService(AgentID);
            
            int count = 100;
            while (!clientCaps.CallbackHasCome && count > 0)
            {
                //m_log.Debug("  >>> Waiting... " + count);
                if (clientCaps.RequestToCancelTeleport)
                {
                    //If the call was canceled, we need to break here 
                    //   now and tell the code that called us about it
                    callWasCanceled = true;
                    return true;
                }
                Thread.Sleep(100);
                count--;
            }
            //If we made it through the whole loop, we havn't been canceled,
            //    as we either have timed out or made it, so no checks are needed
            callWasCanceled = false;
            return clientCaps.CallbackHasCome;
        }

        protected bool WaitForCallback(UUID AgentID)
        {
            IClientCapsService clientCaps = m_registry.RequestModuleInterface<ICapsService>().GetClientCapsService(AgentID);

            int count = 100;
            while (!clientCaps.CallbackHasCome && count > 0)
            {
                //m_log.Debug("  >>> Waiting... " + count);
                Thread.Sleep(100);
                count--;
            }
            return clientCaps.CallbackHasCome;
        }

        #endregion

        #endregion

        #region Crossing

        protected bool CrossAgent(GridRegion crossingRegion, Vector3 pos,
            Vector3 velocity, AgentCircuitData circuit, AgentData cAgent, UUID AgentID, ulong requestingRegion)
        {
            IClientCapsService clientCaps = m_registry.RequestModuleInterface<ICapsService>().GetClientCapsService(AgentID);
            IRegionClientCapsService requestingRegionCaps = clientCaps.GetCapsService(requestingRegion);
            ISimulationService SimulationService = m_registry.RequestModuleInterface<ISimulationService>();
            if (SimulationService != null)
            {
                //Note: we have to pull the new grid region info as the one from the region cannot be trusted
                IGridService GridService = m_registry.RequestModuleInterface<IGridService>();
                if (GridService != null)
                {
                    //Set the user in transit so that we block duplicate tps and reset any cancelations
                    if (!SetUserInTransit(AgentID))
                        return false;

                    bool result = false;

                    crossingRegion = GridService.GetRegionByUUID(UUID.Zero, crossingRegion.RegionID);
                    if (!SimulationService.UpdateAgent(crossingRegion, cAgent))
                    {
                        m_log.Warn("[EventQueue]: Failed to cross agent " + AgentID + " because region did not accept it. Resetting.");
                    }
                    else
                    {
                        IEventQueueService EQService = m_registry.RequestModuleInterface<IEventQueueService>();
                        uint x, y;
                        Utils.LongToUInts(requestingRegion, out x, out y);

                        //Add this for the viewer, but not for the sim, seems to make the viewer happier
                        int XOffset = crossingRegion.RegionLocX - (int)x;
                        pos.X += XOffset;

                        int YOffset = crossingRegion.RegionLocY - (int)y;
                        pos.Y += YOffset;

                        IRegionClientCapsService otherRegion = clientCaps.GetCapsService(crossingRegion.RegionHandle);
                        //Tell the client about the transfer
                        EQService.CrossRegion(crossingRegion.RegionHandle, pos, velocity, crossingRegion.ExternalEndPoint, otherRegion.CapsUrl,
                                           AgentID, circuit.SessionID,
                                           crossingRegion.RegionSizeX, crossingRegion.RegionSizeY,
                                           requestingRegion);

                        result = WaitForCallback(AgentID);
                        if (!result)
                            m_log.Warn("[EntityTransferModule]: Callback never came in crossing agent " + circuit.AgentID + ". Resetting.");
                        else
                        {
                            // Next, let's close the child agent connections that are too far away.
                            INeighborService service = m_registry.RequestModuleInterface<INeighborService>();
                            if (service != null)
                            {
                                //Fix the root agent status
                                otherRegion.RootAgent = true;
                                requestingRegionCaps.RootAgent = false;

                                service.CloseNeighborAgents(crossingRegion.RegionLocX, crossingRegion.RegionLocY, AgentID, requestingRegion);
                            }
                        }
                    }

                    //All done
                    ResetFromTransit(AgentID);
                    return result;
                }
            }
            return false;
        }

        #endregion

        #region Agent Update

        protected void SendChildAgentUpdate(AgentPosition agentpos, UUID regionID)
        {
            //We need to send this update out to all the child agents this region has
            INeighborService service = m_registry.RequestModuleInterface<INeighborService>();
            if (service != null)
            {
                ISimulationService SimulationService = m_registry.RequestModuleInterface<ISimulationService>();
                if (SimulationService != null)
                {
                    GridRegion ourRegion = m_registry.RequestModuleInterface<IGridService>().GetRegionByUUID(UUID.Zero, regionID);
                    if (ourRegion == null)
                    {
                        m_log.Info("[EQMService]: Failed to inform neighbors about updating agent, could not find our region. ");
                        return;
                    }
                    List<GridRegion> ourNeighbors = service.GetNeighbors(ourRegion);
                    foreach (GridRegion region in ourNeighbors)
                    {
                        if (!SimulationService.UpdateAgent(region, agentpos))
                        {
                            m_log.Info("[EQMService]: Failed to inform " + region.RegionName + " about updating agent. ");
                        }
                    }
                }
            }
        }

        #endregion

        #endregion
    }
}
