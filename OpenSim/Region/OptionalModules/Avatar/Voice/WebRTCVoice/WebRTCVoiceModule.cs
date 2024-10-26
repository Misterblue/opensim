/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Web;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using Mono.Addins;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

using log4net;
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Region.OptionalModules.Avatar.Voice.WebRTCVoice
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WebRTCVoiceModule")]
    /// <summary>
    /// This module provides the WebRTC voice service for OpenSimulator.
    /// 
    /// In particular, it proivdes the following capabilities:
    ///      ProvisionVoiceAccountRequest, VoiceSignalingRequest, and ParcelVoiceInfoRequest.    
    /// which are the user interface to the voice service.
    /// 
    /// Initially, when the user connects to the region, the region feature "VoiceServiceType" is
    /// set to "webrtc" and the capabilities that support voice are enabled.
    /// The capabilities then pass the user request information to the IWebRtcVoiceService interface
    /// that has been registered for the reqion.
    /// </summary>
    public class WebRTCVoiceModule : ISharedRegionModule, IVoiceModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string logHeader = "[WebRTC Voice]";

        // Control info
        private static bool m_Enabled = false;
        string m_openSimWellKnownHTTPAddress;
        uint m_ServicePort;

        private readonly Dictionary<string, string> m_UUIDName = new Dictionary<string, string>();
        private Dictionary<string, string> m_ParcelAddress = new Dictionary<string, string>();

        private IConfig m_Config;

        public void Initialise(IConfigSource config)
        {
            m_Config = config.Configs["WebRtcVoice"];

            if (m_Config is null)
                return;

            if (!m_Config.GetBoolean("Enabled", false))
                return;

            try
            {
                string serviceDll = m_Config.GetString("LocalServiceUrl", String.Empty);

                if (serviceDll.Length == 0)
                {
                    m_log.ErrorFormat("{0}: No LocalServiceUrl named in section WebRTCVoice.  Not starting.", logHeader);
                    return;
                }

                // TODO:


                m_Enabled = true;

                m_log.Info($"{logHeader}: plugin enabled");
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: plugin initialization failed: {1} {2}", logHeader, e.Message, e.StackTrace);
                return;
            }
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            // We generate these like this: The region's external host name
            // as defined in Regions.ini is a good address to use. It's a
            // dotted quad (or should be!) and it can reach this host from
            // a client. The port is grabbed from the region's HTTP server.
            m_openSimWellKnownHTTPAddress = scene.RegionInfo.ExternalHostName;
            m_ServicePort = MainServer.Instance.Port;

            if (m_Enabled)
            {
                // we need to capture scene in an anonymous method
                // here as we need it later in the callbacks
                scene.EventManager.OnRegisterCaps += (UUID agentID, Caps caps) =>
                    {
                        OnRegisterCaps(scene, agentID, caps);
                    };
            }
        }

        public void RemoveRegion(Scene scene)
        {
            var sfm = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            sfm.OnSimulatorFeaturesRequest -= OnSimulatorFeatureRequestHandler;
        }

        public void RegionLoaded(Scene scene)
        {
            if (m_Enabled)
            {
                m_log.Info($"{logHeader}: registering IVoiceModule with the scene");

                // register the voice interface for this module, so the script engine can call us
                scene.RegisterModuleInterface<IVoiceModule>(this);

                var sfm = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
                sfm.OnSimulatorFeaturesRequest += OnSimulatorFeatureRequestHandler;
                m_log.DebugFormat("{0}: registering OnSimulatorFeatureRequestHandler", logHeader);
            }
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "WebRTCVoiceModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        // <summary>
        // implementation of IVoiceModule, called by osSetParcelSIPAddress script function
        // </summary>
        public void setLandSIPAddress(string SIPAddress,UUID GlobalID)
        {
            m_log.DebugFormat("{0}: setLandSIPAddress parcel id {1}: setting sip address {2}",
                                  logHeader, GlobalID, SIPAddress);

            lock (m_ParcelAddress)
            {
                if (m_ParcelAddress.ContainsKey(GlobalID.ToString()))
                {
                    m_ParcelAddress[GlobalID.ToString()] = SIPAddress;
                }
                else
                {
                    m_ParcelAddress.Add(GlobalID.ToString(), SIPAddress);
                }
            }
        }

        // Called when the simulator features are being constructed.
        // Add the flag that says we support WebRTC voice.
        private void OnSimulatorFeatureRequestHandler(UUID agentID, ref OSDMap features)
        {
            m_log.DebugFormat("{0}: setting VoiceServerType=webrtc for agent {1}", logHeader, agentID);
            features["VoiceServerType"] = "webrtc";
        }

        // <summary>
        // OnRegisterCaps is invoked via the scene.EventManager
        // everytime OpenSim hands out capabilities to a client
        // (login, region crossing). We contribute three capabilities to
        // the set of capabilities handed back to the client:
        // ProvisionVoiceAccountRequest, VoiceSignalingRequest, and ParcelVoiceInfoRequest.
        //
        // ProvisionVoiceAccountRequest allows the client to obtain
        // the voice account credentials for the avatar it is
        // controlling (e.g., user name, password, etc).
        //
        // VoiceSignalingRequest: Used for trickling ICE candidates.
        //
        // ParcelVoiceInfoRequest is invoked whenever the client
        // changes from one region or parcel to another.
        //
        // Note that OnRegisterCaps is called here via a closure
        // delegate containing the scene of the respective region (see
        // Initialise()).
        // </summary>
        public void OnRegisterCaps(Scene scene, UUID agentID, Caps caps)
        {
            m_log.DebugFormat(
                "{0}: OnRegisterCaps() called with agentID {1} caps {2} in scene {3}",
                logHeader, agentID, caps, scene.RegionInfo.RegionName);

            caps.RegisterSimpleHandler("ProvisionVoiceAccountRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        ProvisionVoiceAccountRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            caps.RegisterSimpleHandler("VoiceSignalingRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        VoiceSignalingRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            caps.RegisterSimpleHandler("ParcelVoiceInfoRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        ParcelVoiceInfoRequest(httpRequest, httpResponse, agentID, scene);
                    }));

        }

        /// <summary>
        /// Callback for a client request for Voice Account Details
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ProvisionVoiceAccountRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if(request.HttpMethod != "POST")
            {
                m_log.DebugFormat("[{0}][ProvisionVoice]: Not a POST request. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            m_log.DebugFormat("{0}[ProvisionVoice]: Request for {1}", logHeader, agentID.ToString());

            // Deserialize the request
            OSDMap map = null;
            using (Stream inputStream = request.InputStream)
            {
                if (inputStream.Length > 0)
                {
                    OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                    m_log.DebugFormat("{0}[ProvisionVoice]: Request: {1}", logHeader, tmp.ToString());

                    if (tmp is OSDMap)
                    {
                        map = (OSDMap)tmp;
                    }
                }
            }
            if (map is null)
            {
                m_log.DebugFormat("{0}[ProvisionVoice]: No request data found. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // Make sure the request is for WebRTC voice
            if (map.TryGetValue("voice_server_type", out OSD vstosd))
            {
                if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }
            }

            // See if a 'logout' request is present
            if (map.TryGetValue("logout", out OSD logout))
            {
                if (logout is OSDBoolean lob && lob)
                {
                    m_log.DebugFormat("[{0}][ProvisionVoice]: avatar \"{1}\": logout", logHeader, agentID);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }
            }

            // TODO:
            m_log.DebugFormat("{0}[ProvisionVoice]: message: {1}", logHeader, map.ToString());

            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
            return;

            /* OLD FREE SWITCH CODE -- REMOVE REMOVE REMOVE
            ScenePresence avatar = scene.GetScenePresence(agentID);
            if (avatar == null)
            {
                System.Threading.Thread.Sleep(2000);
                avatar = scene.GetScenePresence(agentID);

                if (avatar == null)
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd>undef</llsd>");
                    return;
                }
            }
            string avatarName = avatar.Name;

            try
            {
                //XmlElement    resp;
                string agentname = "x" + Convert.ToBase64String(agentID.GetBytes());
                string password  = "1234";//temp hack//new UUID(Guid.NewGuid()).ToString().Replace('-','Z').Substring(0,16);

                // XXX: we need to cache the voice credentials, as
                // FreeSwitch is later going to come and ask us for
                // those
                agentname = agentname.Replace('+', '-').Replace('/', '_');

                lock (m_UUIDName)
                {
                    if (m_UUIDName.ContainsKey(agentname))
                    {
                        m_UUIDName[agentname] = avatarName;
                    }
                    else
                    {
                        m_UUIDName.Add(agentname, avatarName);
                    }
                }

                string accounturl = String.Format("http://{0}:{1}{2}/", m_openSimWellKnownHTTPAddress,
                                                              m_freeSwitchServicePort, m_freeSwitchAPIPrefix);
                // fast foward encode
                osUTF8 lsl = LLSDxmlEncode2.Start();
                LLSDxmlEncode2.AddMap(lsl);
                LLSDxmlEncode2.AddElem("username", agentname, lsl);
                LLSDxmlEncode2.AddElem("password", password, lsl);
                LLSDxmlEncode2.AddElem("voice_sip_uri_hostname", m_freeSwitchRealm, lsl);
                LLSDxmlEncode2.AddElem("voice_account_server_name", accounturl, lsl);
                LLSDxmlEncode2.AddEndMap(lsl);
                response.RawBuffer = LLSDxmlEncode2.EndToBytes(lsl);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[FreeSwitchVoice][ProvisionVoice]: avatar \"{0}\": {1}, retry later", avatarName, e.Message);
                m_log.DebugFormat("[FreeSwitchVoice][ProvisionVoice]: avatar \"{0}\": {1} failed", avatarName, e.ToString());

                response.RawBuffer = osUTF8.GetASCIIBytes("<llsd>undef</llsd>");
            }
            */
        }
        public void VoiceSignalingRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if(request.HttpMethod != "POST")
            {
                m_log.DebugFormat("[{0}][VoiceSignaling]: Not a POST request. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            m_log.DebugFormat("{0}[VoiceSignaling]: Request for {1}", logHeader, agentID.ToString());

            // Deserialize the request
            OSDMap map = null;
            using (Stream inputStream = request.InputStream)
            {
                if (inputStream.Length > 0)
                {
                    OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                    m_log.DebugFormat("{0}[VoiceSignaling]: Request: {1}", logHeader, tmp.ToString());

                    if (tmp is OSDMap)
                    {
                        map = (OSDMap)tmp;
                    }
                }
            }
            if (map is null)
            {
                m_log.DebugFormat("{0}[VoiceSignaling]: No request data found. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // Make sure the request is for WebRTC voice
            if (map.TryGetValue("voice_server_type", out OSD vstosd))
            {
                if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }
            }
            m_log.DebugFormat("{0}[VoiceSignaling]: message: {1}", logHeader, map.ToString());

            // TODO:
            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
            return;
        }

        /// <summary>
        /// Callback for a client request for ParcelVoiceInfo
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ParcelVoiceInfoRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;

            m_log.DebugFormat(
                "{0}[PARCELVOICE]: ParcelVoiceInfoRequest() on {1} for {2}",
                logHeader, scene.RegionInfo.RegionName, agentID);

            ScenePresence avatar = scene.GetScenePresence(agentID);
            if(avatar == null)
            {
                response.RawBuffer = Util.UTF8.GetBytes("<llsd>undef</llsd>");
                return;
            }

            string avatarName = avatar.Name;

            // - check whether we have a region channel in our cache
            // - if not:
            //       create it and cache it
            // - send it to the client
            // - send channel_uri: as "sip:regionID@m_sipDomain"
            try
            {
                string channelUri;

                if (null == scene.LandChannel)
                {
                    m_log.ErrorFormat("region \"{0}\": avatar \"{1}\": land data not yet available",
                                                      scene.RegionInfo.RegionName, avatarName);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd>undef</llsd>");
                    return;
                }

                // get channel_uri: check first whether estate
                // settings allow voice, then whether parcel allows
                // voice, if all do retrieve or obtain the parcel
                // voice channel
                LandData land = scene.GetLandData(avatar.AbsolutePosition);

                // TODO: EstateSettings don't seem to get propagated...
                 if (!scene.RegionInfo.EstateSettings.AllowVoice)
                 {
                     m_log.DebugFormat("{0}[PARCELVOICE]: region \"{1}\": voice not enabled in estate settings",
                                       logHeader, scene.RegionInfo.RegionName);
                    channelUri = String.Empty;
                }
                else

                if (!scene.RegionInfo.EstateSettings.TaxFree && (land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                {
                    channelUri = String.Empty;
                }
                else
                {
                    channelUri = ChannelUri(scene, land);
                }

                // fast foward encode
                osUTF8 lsl = LLSDxmlEncode2.Start(512);
                LLSDxmlEncode2.AddMap(lsl);
                LLSDxmlEncode2.AddElem("parcel_local_id", land.LocalID, lsl);
                LLSDxmlEncode2.AddElem("region_name", scene.Name, lsl);
                LLSDxmlEncode2.AddMap("voice_credentials", lsl);
                LLSDxmlEncode2.AddElem("channel_uri", channelUri, lsl);
                //LLSDxmlEncode2.AddElem("channel_credentials", channel_credentials, lsl);
                LLSDxmlEncode2.AddEndMap(lsl);
                LLSDxmlEncode2.AddEndMap(lsl);

                response.RawBuffer= LLSDxmlEncode2.EndToBytes(lsl);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}[PARCELVOICE]: region \"{1}\": avatar \"{2}\": {3}, retry later",
                                  logHeader, scene.RegionInfo.RegionName, avatarName, e.Message);
                m_log.DebugFormat("{0}[PARCELVOICE]: region \"{1}\": avatar \"{2}\": {3} failed",
                                  logHeader, scene.RegionInfo.RegionName, avatarName, e.ToString());

                response.RawBuffer = Util.UTF8.GetBytes("<llsd>undef</llsd>");
            }
        }

        // Not sure what this Uri is for. Is this FreeSwitch specific?
        // TODO: is this useful for WebRTC?
        private string ChannelUri(Scene scene, LandData land)
        {
            string channelUri = null;

            string landUUID;
            string landName;

            // Create parcel voice channel. If no parcel exists, then the voice channel ID is the same
            // as the directory ID. Otherwise, it reflects the parcel's ID.

            lock (m_ParcelAddress)
            {
                if (m_ParcelAddress.ContainsKey(land.GlobalID.ToString()))
                {
                    m_log.DebugFormat("{0}: parcel id {1}: using sip address {2}",
                                      logHeader, land.GlobalID, m_ParcelAddress[land.GlobalID.ToString()]);
                    return m_ParcelAddress[land.GlobalID.ToString()];
                }
            }

            if (land.LocalID != 1 && (land.Flags & (uint)ParcelFlags.UseEstateVoiceChan) == 0)
            {
                landName = String.Format("{0}:{1}", scene.RegionInfo.RegionName, land.Name);
                landUUID = land.GlobalID.ToString();
                m_log.DebugFormat("{0}: Region:Parcel \"{1}\": parcel id {2}: using channel name {3}",
                                  logHeader, landName, land.LocalID, landUUID);
            }
            else
            {
                landName = String.Format("{0}:{1}", scene.RegionInfo.RegionName, scene.RegionInfo.RegionName);
                landUUID = scene.RegionInfo.RegionID.ToString();
                m_log.DebugFormat("{0}: Region:Parcel \"{1}\": parcel id {2}: using channel name {3}",
                                  logHeader, landName, land.LocalID, landUUID);
            }

            // slvoice handles the sip address differently if it begins with confctl, hiding it from the user in
            // the friends list. however it also disables the personal speech indicators as well unless some
            // siren14-3d codec magic happens. we dont have siren143d so we'll settle for the personal speech indicator.
            channelUri = String.Format("sip:conf-{0}@{1}",
                     "x" + Convert.ToBase64String(Encoding.ASCII.GetBytes(landUUID)),
                     /*m_freeSwitchRealm*/ "webRTC");

            lock (m_ParcelAddress)
            {
                if (!m_ParcelAddress.ContainsKey(land.GlobalID.ToString()))
                {
                    m_ParcelAddress.Add(land.GlobalID.ToString(),channelUri);
                }
            }

            return channelUri;
        }

    }
}
