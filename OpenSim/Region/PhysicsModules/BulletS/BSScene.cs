/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyrightD
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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using Nini.Config;
using log4net;
using OpenMetaverse;
using Mono.Addins;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "BulletSPhysicsScene")]
    public sealed class BSScene : PhysicsScene, IPhysicsParameters, INonSharedRegionModule
    {
        internal static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        internal static readonly string LogHeader = "[BULLETS SCENE]";

        private bool m_Enabled = false;
        private IConfigSource m_Config;

        // The name of the region we're working for.
        public string RegionName { get; private set; }

        // The handle to the underlying managed or unmanaged version of Bullet being used.
        public string BulletEngineName { get; private set; }
        public BSAPITemplate PE;

        // If the physics engine is running on a separate thread
        public Thread m_physicsThread;

        public Dictionary<uint, BSPhysObject> PhysObjects;
        public BSShapeCollection Shapes;

        // Keeping track of the objects with collisions so we can report begin and end of a collision
        public HashSet<BSPhysObject> ObjectsWithCollisions = new HashSet<BSPhysObject>();
        public HashSet<BSPhysObject> ObjectsWithNoMoreCollisions = new HashSet<BSPhysObject>();

        // All the collision processing is protected with this lock object
        public Object CollisionLock = new Object();

        // Properties are updated here
        public Object UpdateLock = new Object();
        public HashSet<BSPhysObject> ObjectsWithUpdates = new HashSet<BSPhysObject>();

        // Keep track of all the avatars so we can send them a collision event
        //    every tick so OpenSim will update its animation.
        private HashSet<BSPhysObject> AvatarsInScene = new HashSet<BSPhysObject>();
        private Object AvatarsInSceneLock = new Object();

        // let my minuions use my logger
        public ILog Logger { get { return m_log; } }

        public IMesher mesher;
        public uint WorldID { get; private set; }
        public BulletWorld World { get; private set; }

        // All the constraints that have been allocated in this instance.
        public BSConstraintCollection Constraints { get; private set; }

        // Simulation parameters
        //internal float m_physicsStepTime;   // if running independently, the interval simulated by default

        internal int m_maxSubSteps;
        internal float m_fixedTimeStep;

        internal float m_simulatedTime;     // the time simulated previously. Used for physics framerate calc.

        internal long m_simulationStep = 0; // The current simulation step.
        public long SimulationStep { get { return m_simulationStep; } }
        // A number to use for SimulationStep that is probably not any step value
        // Used by the collision code (which remembers the step when a collision happens) to remember not any simulation step.
        public static long NotASimulationStep = -1234;

        internal float LastTimeStep { get; private set; }   // The simulation time from the last invocation of Simulate()

        internal float NominalFrameRate { get; set; }       // Parameterized ideal frame rate that simulation is scaled to

        // Physical objects can register for prestep or poststep events
        public delegate void PreStepAction(float timeStep);
        public delegate void PostStepAction(float timeStep);
        public event PreStepAction BeforeStep;
        public event PostStepAction AfterStep;

        // A value of the time 'now' so all the collision and update routines do not have to get their own
        // Set to 'now' just before all the prims and actors are called for collisions and updates
        public int SimulationNowTime { get; private set; }

        // True if initialized and ready to do simulation steps
        private bool m_initialized = false;

        // Object locked whenever execution is inside the physics engine
        public Object PhysicsEngineLock = new object();
        // Flag that is true when the simulator is active and shouldn't be touched
        public bool InSimulationTime { get; private set; }

        // Pinned memory used to pass step information between managed and unmanaged
        internal int m_maxCollisionsPerFrame;
        internal CollisionDesc[] m_collisionArray;

        internal int m_maxUpdatesPerFrame;
        internal EntityProperties[] m_updateArray;

        /// <summary>
        /// Used to control physics simulation timing if Bullet is running on its own thread.
        /// </summary>
        private ManualResetEvent m_updateWaitEvent;

        public const uint TERRAIN_ID = 0;       // OpenSim senses terrain with a localID of zero
        public const uint GROUNDPLANE_ID = 1;
        public const uint CHILDTERRAIN_ID = 2;  // Terrain allocated based on our mega-prim childre start here

        public float SimpleWaterLevel { get; set; }
        public BSTerrainManager TerrainManager { get; private set; }

        public ConfigurationParameters Params
        {
            get { return UnmanagedParams[0]; }
        }
        public Vector3 DefaultGravity
        {
            get { return new Vector3(0f, 0f, Params.gravity); }
        }
        // Just the Z value of the gravity
        public float DefaultGravityZ
        {
            get { return Params.gravity; }
        }

        // When functions in the unmanaged code must be called, it is only
        //   done at a known time just before the simulation step. The taint
        //   system saves all these function calls and executes them in
        //   order before the simulation.
        public delegate void TaintCallback();
        private struct TaintCallbackEntry
        {
            public String originator;
            public String ident;
            public TaintCallback callback;
            public TaintCallbackEntry(string pIdent, TaintCallback pCallBack)
            {
                originator = BSScene.DetailLogZero;
                ident = pIdent;
                callback = pCallBack;
            }
            public TaintCallbackEntry(string pOrigin, string pIdent, TaintCallback pCallBack)
            {
                originator = pOrigin;
                ident = pIdent;
                callback = pCallBack;
            }
        }
        private Object _taintLock = new Object();   // lock for using the next object
        private List<TaintCallbackEntry> _taintOperations;
        private Dictionary<string, TaintCallbackEntry> _postTaintOperations;
        private List<TaintCallbackEntry> _postStepOperations;

        // A pointer to an instance if this structure is passed to the C++ code
        // Used to pass basic configuration values to the unmanaged code.
        internal ConfigurationParameters[] UnmanagedParams;

        // Sometimes you just have to log everything.
        public LogWriter PhysicsLogging;
        private bool m_physicsLoggingEnabled;
        private string m_physicsLoggingDir;
        private string m_physicsLoggingPrefix;
        private int m_physicsLoggingFileMinutes;
        private bool m_physicsLoggingDoFlush;
        private bool m_physicsPhysicalDumpEnabled;
        public int PhysicsMetricDumpFrames { get; set; }
        // 'true' of the vehicle code is to log lots of details
        public bool VehicleLoggingEnabled { get; private set; }
        public bool VehiclePhysicalLoggingEnabled { get; private set; }

        #region INonSharedRegionModule
        public string Name
        {
            get { return "BulletSim"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            // TODO: Move this out of Startup
            IConfig config = source.Configs["Startup"];
            if (config != null)
            {
                string physics = config.GetString("physics", string.Empty);
                if (physics == Name)
                {
                    string mesher = config.GetString("meshing", string.Empty);
                    if (string.IsNullOrEmpty(mesher) || !mesher.Equals("Meshmerizer"))
                    {
                        m_log.Error("[BulletSim] Opensim.ini meshing option must be set to \"Meshmerizer\"");
                        throw new Exception("Invalid physics meshing option");
                    }

                    m_Enabled = true;
                    m_Config = source;
                }
            }

        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            RegionName = scene.RegionInfo.RegionName;
            PhysicsSceneName = Name + "/" + RegionName;

            scene.RegisterModuleInterface<PhysicsScene>(this);
            Vector3 extent = new Vector3(scene.RegionInfo.RegionSizeX, scene.RegionInfo.RegionSizeY, scene.RegionInfo.RegionSizeZ);
            Initialise(m_Config, extent);

            base.Initialise(scene.PhysicsRequestAsset,
                (scene.Heightmap != null ? scene.Heightmap.GetFloatsSerialised() : new float[scene.RegionInfo.RegionSizeX * scene.RegionInfo.RegionSizeY]),
                (float)scene.RegionInfo.RegionSettings.WaterHeight);

            EngineName = Name + " " + PE.BulletEngineVersion;
            EngineType = Name;
            // the above usually sets:
            //  EngineType = "BulletSim"
            //  EngineName = "BulletSim 1.1,3.25" with the version being "BulletsimWrapperVersion,BulletPhysicsEngineVersion"
            //  "EngineName" is returned by the LSL function "osGetPhysicsEngineName"
            //  "EngineType" is returned by the LSL function "osGetPhysicsEngineType"
            //  Note that there is also "BulletEngineName" which comes from the parameter files and
            //    specifies which Bullet DLL to load.
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            mesher = scene.RequestModuleInterface<IMesher>();
            if (mesher == null)
                m_log.WarnFormat("{0} No mesher. Things will not work well.", LogHeader);

            scene.PhysicsEnabled = true;
        }
        #endregion

        #region Initialization

        private void Initialise(IConfigSource config, Vector3 regionExtent)
        {
            _taintOperations = new List<TaintCallbackEntry>();
            _postTaintOperations = new Dictionary<string, TaintCallbackEntry>();
            _postStepOperations = new List<TaintCallbackEntry>();
            PhysObjects = new Dictionary<uint, BSPhysObject>();
            Shapes = new BSShapeCollection(this);

            m_simulatedTime = 0f;
            LastTimeStep = 0.1f;

            // Allocate pinned memory to pass parameters.
            UnmanagedParams = new ConfigurationParameters[1];

            // Set default values for physics parameters plus any overrides from the ini file
            GetInitialParameterValues(config);

            // Force some parameters to values depending on other configurations
            // Only use heightmap terrain implementation if terrain larger than legacy size
            if ((uint)regionExtent.X > Constants.RegionSize || (uint)regionExtent.Y > Constants.RegionSize)
            {
                m_log.WarnFormat("{0} Forcing terrain implementation to heightmap for large region", LogHeader);
                BSParam.TerrainImplementation = (float)BSTerrainPhys.TerrainImplementation.Heightmap;
            }

            // Get the connection to the physics engine (could be native or one of many DLLs)
            PE = SelectUnderlyingBulletEngine(BulletEngineName);

            // Enable very detailed logging.
            // By creating an empty logger when not logging, the log message invocation code
            //     can be left in and every call doesn't have to check for null.
            if (m_physicsLoggingEnabled)
            {
                PhysicsLogging = new LogWriter(m_physicsLoggingDir, m_physicsLoggingPrefix, m_physicsLoggingFileMinutes, m_physicsLoggingDoFlush);
                PhysicsLogging.ErrorLogger = m_log; // for DEBUG. Let's the logger output its own error messages.
            }
            else
            {
                PhysicsLogging = new LogWriter();
            }

            // Allocate memory for returning of the updates and collisions from the physics engine
            m_collisionArray = new CollisionDesc[m_maxCollisionsPerFrame];
            m_updateArray = new EntityProperties[m_maxUpdatesPerFrame];

            // The bounding box for the simulated world. The origin is 0,0,0 unless we're
            //    a child in a mega-region.
            // Bullet actually doesn't care about the extents of the simulated
            //    area. It tracks active objects no matter where they are.
            Vector3 worldExtent = regionExtent;

            World = PE.Initialize(worldExtent, Params, m_maxCollisionsPerFrame, ref m_collisionArray, m_maxUpdatesPerFrame, ref m_updateArray);

            Constraints = new BSConstraintCollection(World);

            TerrainManager = new BSTerrainManager(this, worldExtent);
            TerrainManager.CreateInitialGroundPlaneAndTerrain();

            // Put some informational messages into the log file.
            m_log.InfoFormat("{0} Linksets implemented with {1}", LogHeader, (BSLinkset.LinksetImplementation)BSParam.LinksetImplementation);

            InSimulationTime = false;
            m_initialized = true;

            // If the physics engine runs on its own thread, start same.
            if (BSParam.UseSeparatePhysicsThread)
            {
                // The physics simulation should happen independently of the heartbeat loop
                m_physicsThread = WorkManager.StartThread(
                        BulletSPluginPhysicsThread,
                        string.Format("{0} ({1})", BulletEngineName, RegionName));
            }
        }

        // All default parameter values are set here. There should be no values set in the
        // variable definitions.
        private void GetInitialParameterValues(IConfigSource config)
        {
            ConfigurationParameters parms = new ConfigurationParameters();
            UnmanagedParams[0] = parms;

            BSParam.SetParameterDefaultValues(this);

            if (config != null)
            {
                // If there are specifications in the ini file, use those values
                IConfig pConfig = config.Configs["BulletSim"];
                if (pConfig != null)
                {
                    BSParam.SetParameterConfigurationValues(this, pConfig);

                    // There are two Bullet implementations to choose from
                    BulletEngineName = pConfig.GetString("BulletEngine", "BulletUnmanaged");

                    // Very detailed logging for physics debugging
                    // TODO: the boolean values can be moved to the normal parameter processing.
                    m_physicsLoggingEnabled = pConfig.GetBoolean("PhysicsLoggingEnabled", false);
                    m_physicsLoggingDir = pConfig.GetString("PhysicsLoggingDir", ".");
                    m_physicsLoggingPrefix = pConfig.GetString("PhysicsLoggingPrefix", "physics-%REGIONNAME%-");
                    m_physicsLoggingFileMinutes = pConfig.GetInt("PhysicsLoggingFileMinutes", 5);
                    m_physicsLoggingDoFlush = pConfig.GetBoolean("PhysicsLoggingDoFlush", false);
                    m_physicsPhysicalDumpEnabled = pConfig.GetBoolean("PhysicsPhysicalDumpEnabled", false);
                    // Very detailed logging for vehicle debugging
                    VehicleLoggingEnabled = pConfig.GetBoolean("VehicleLoggingEnabled", false);
                    VehiclePhysicalLoggingEnabled = pConfig.GetBoolean("VehiclePhysicalLoggingEnabled", false);

                    // Do any replacements in the parameters
                    m_physicsLoggingPrefix = m_physicsLoggingPrefix.Replace("%REGIONNAME%", RegionName);
                }
                else
                {
                    // Nothing in the configuration INI file so assume unmanaged and other defaults.
                    BulletEngineName = "BulletUnmanaged";
                    m_physicsLoggingEnabled = false;
                    VehicleLoggingEnabled = false;
                }

                // The material characteristics.
                BSMaterials.InitializeFromDefaults(Params);
                if (pConfig != null)
                {
                    // Let the user add new and interesting material property values.
                    BSMaterials.InitializefromParameters(pConfig);
                }
            }
        }

        // A helper function that handles a true/false parameter and returns the proper float number encoding
        float ParamBoolean(IConfig config, string parmName, float deflt)
        {
            float ret = deflt;
            if (config.Contains(parmName))
            {
                ret = ConfigurationParameters.numericFalse;
                if (config.GetBoolean(parmName, false))
                {
                    ret = ConfigurationParameters.numericTrue;
                }
            }
            return ret;
        }

        // Select the connection to the actual Bullet implementation.
        // The main engine selection is the engineName up to the first hypen.
        // So "Bullet-2.80-OpenCL-Intel" specifies the 'bullet' class here and the whole name
        //     is passed to the engine to do its special selection, etc.
        private BSAPITemplate SelectUnderlyingBulletEngine(string engineName)
        {
            // For the moment, do a simple switch statement.
            // Someday do fancyness with looking up the interfaces in the assembly.
            BSAPITemplate ret = null;

            string selectionName = engineName.ToLower();
            int hyphenIndex = engineName.IndexOf("-");
            if (hyphenIndex > 0)
                selectionName = engineName.ToLower().Substring(0, hyphenIndex - 1);

            switch (selectionName)
            {
                case "bullet":
                case "bulletunmanaged":
                    ret = new BSAPIUnman(engineName, this);
                    break;
                case "bulletxna":
                    ret = new BSAPIXNA(engineName, this);
                    // Disable some features that are not implemented in BulletXNA
                    m_log.InfoFormat("{0} Disabling some physics features not implemented by BulletXNA", LogHeader);
                    m_log.InfoFormat("{0}    Disabling ShouldUseBulletHACD", LogHeader);
                    BSParam.ShouldUseBulletHACD = false;
                    m_log.InfoFormat("{0}    Disabling ShouldUseSingleConvexHullForPrims", LogHeader);
                    BSParam.ShouldUseSingleConvexHullForPrims = false;
                    m_log.InfoFormat("{0}    Disabling ShouldUseGImpactShapeForPrims", LogHeader);
                    BSParam.ShouldUseGImpactShapeForPrims = false;
                    m_log.InfoFormat("{0}    Setting terrain implimentation to Heightmap", LogHeader);
                    BSParam.TerrainImplementation = (float)BSTerrainPhys.TerrainImplementation.Heightmap;
                    break;
            }

            if (ret == null)
            {
                m_log.ErrorFormat("{0} COULD NOT SELECT BULLET ENGINE: '[BulletSim]PhysicsEngine' must be either 'BulletUnmanaged-*' or 'BulletXNA-*'", LogHeader);
            }
            else
            {
                m_log.InfoFormat("{0} Selected bullet engine {1} -> {2}/{3}", LogHeader, engineName, ret.BulletEngineName, ret.BulletEngineVersion);
            }

            return ret;
        }

        public override void Dispose()
        {
            // m_log.DebugFormat("{0}: Dispose()", LogHeader);

            // make sure no stepping happens while we're deleting stuff
            m_initialized = false;

            lock (PhysObjects)
            {
                foreach (KeyValuePair<uint, BSPhysObject> kvp in PhysObjects)
                {
                    kvp.Value.Destroy();
                }
                PhysObjects.Clear();
            }

            // Now that the prims are all cleaned up, there should be no constraints left
            if (Constraints != null)
            {
                Constraints.Dispose();
                Constraints = null;
            }

            if (Shapes != null)
            {
                Shapes.Dispose();
                Shapes = null;
            }

            if (TerrainManager != null)
            {
                TerrainManager.ReleaseGroundPlaneAndTerrain();
                TerrainManager.Dispose();
                TerrainManager = null;
            }

            // Anything left in the unmanaged code should be cleaned out
            PE.Shutdown(World);

            // Not logging any more
            PhysicsLogging.Close();
        }
        #endregion // Construction and Initialization

        #region Prim and Avatar addition and removal

        public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
        {
            m_log.ErrorFormat("{0}: CALL TO AddAvatar in BSScene. NOT IMPLEMENTED", LogHeader);
            return null;
        }

        public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 size, float footOffset, bool isFlying)
        {
            // m_log.DebugFormat("{0}: AddAvatar: {1}", LogHeader, avName);

            if (!m_initialized) return null;

            BSCharacter actor = new BSCharacter(localID, avName, this, position, Vector3.Zero, size, footOffset, isFlying);
            lock (PhysObjects)
                PhysObjects.Add(localID, actor);

            // TODO: Remove kludge someday.
            // We must generate a collision for avatars whether they collide or not.
            // This is required by OpenSim to update avatar animations, etc.
            lock (AvatarsInSceneLock)
                AvatarsInScene.Add(actor);

            return actor;
        }

        public override void RemoveAvatar(PhysicsActor actor)
        {
            // m_log.DebugFormat("{0}: RemoveAvatar", LogHeader);

            if (!m_initialized) return;

            BSCharacter bsactor = actor as BSCharacter;
            if (bsactor != null)
            {
                try
                {
                    lock (PhysObjects)
                        PhysObjects.Remove(bsactor.LocalID);
                    // Remove kludge someday
                    lock (AvatarsInSceneLock)
                        AvatarsInScene.Remove(bsactor);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("{0}: Attempt to remove avatar that is not in physics scene: {1}", LogHeader, e);
                }
                bsactor.Destroy();
                // bsactor.dispose();
            }
            else
            {
                m_log.ErrorFormat("{0}: Requested to remove avatar that is not a BSCharacter. ID={1}, type={2}",
                                            LogHeader, actor.LocalID, actor.GetType().Name);
            }
        }

        public override void RemovePrim(PhysicsActor prim)
        {
            if (!m_initialized) return;

            BSPhysObject bsprim = prim as BSPhysObject;
            if (bsprim != null)
            {
                DetailLog("{0},RemovePrim,call", bsprim.LocalID);
                // m_log.DebugFormat("{0}: RemovePrim. id={1}/{2}", LogHeader, bsprim.Name, bsprim.LocalID);
                try
                {
                    lock (PhysObjects) PhysObjects.Remove(bsprim.LocalID);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("{0}: Attempt to remove prim that is not in physics scene: {1}", LogHeader, e);
                }
                bsprim.Destroy();
                // bsprim.dispose();
            }
            else
            {
                m_log.ErrorFormat("{0}: Attempt to remove prim that is not a BSPrim type.", LogHeader);
            }
        }

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, uint localID)
        {
            // m_log.DebugFormat("{0}: AddPrimShape2: {1}", LogHeader, primName);

            if (!m_initialized) return null;

            // DetailLog("{0},BSScene.AddPrimShape,call", localID);

            BSPhysObject prim = new BSPrimLinkable(localID, primName, this, position, size, rotation, pbs, isPhysical);
            lock (PhysObjects) PhysObjects.Add(localID, prim);
            return prim;
        }

        #endregion // Prim and Avatar addition and removal

        #region Simulation

        // Call from the simulator to send physics information to the simulator objects.
        // This pushes all the collision and property update events into the objects in
        //    the simulator and, since it is on the heartbeat thread, there is an implicit
        //    locking of those data structures from other heartbeat events.
        // If the physics engine is running on a separate thread, the update information
        //    will be in the ObjectsWithCollions and ObjectsWithUpdates structures.
        public override float Simulate(float timeStep)
        {
            if (!BSParam.UseSeparatePhysicsThread)
            {
                DoPhysicsStep(timeStep);
            }
            return SendUpdatesToSimulator(timeStep);
        }

        // Call the physics engine to do one 'timeStep' and collect collisions and updates
        //    into ObjectsWithCollisions and ObjectsWithUpdates data structures.
        private void DoPhysicsStep(float timeStep)
        {
            // prevent simulation until we've been initialized
            if (!m_initialized) return;

            LastTimeStep = timeStep;

            int updatedEntityCount = 0;
            int collidersCount = 0;

            int beforeTime = Util.EnvironmentTickCount();
            int simTime = 0;
            int numTaints = 0;
            int numSubSteps = 0;

            lock (PhysicsEngineLock)
            {
                InSimulationTime = true;
                // update the prim states while we know the physics engine is not busy
                numTaints += ProcessTaints();

                // Some of the physical objects requre individual, pre-step calls
                //      (vehicles and avatar movement, in particular)
                TriggerPreStepEvent(timeStep);

                // the prestep actions might have added taints
                numTaints += ProcessTaints();

                // The following causes the unmanaged code to output ALL the values found in ALL the objects in the world.
                // Only enable this in a limited test world with few objects.
                if (m_physicsPhysicalDumpEnabled)
                    PE.DumpAllInfo(World);

                // step the physical world one interval
                m_simulationStep++;
                try
                {
                    numSubSteps = PE.PhysicsStep(World, timeStep, m_maxSubSteps, m_fixedTimeStep, out updatedEntityCount, out collidersCount);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("{0},PhysicsStep Exception: nTaints={1}, substeps={2}, updates={3}, colliders={4}, e={5}",
                                LogHeader, numTaints, numSubSteps, updatedEntityCount, collidersCount, e);
                    DetailLog("{0},PhysicsStepException,call, nTaints={1}, substeps={2}, updates={3}, colliders={4}",
                                DetailLogZero, numTaints, numSubSteps, updatedEntityCount, collidersCount);
                    updatedEntityCount = 0;
                    collidersCount = 0;
                }

                // Make the physics engine dump useful statistics periodically
                if (PhysicsMetricDumpFrames != 0 && ((m_simulationStep % PhysicsMetricDumpFrames) == 0))
                    PE.DumpPhysicsStatistics(World);

                InSimulationTime = false;

                // Some actors want to know when the simulation step is complete.
                TriggerPostStepEvent(timeStep);

                // In case there were any parameter updates that happened during the simulation step
                numTaints += ProcessTaints();

                InSimulationTime = false;
            }

            // Get a value for 'now' so all the collision and update routines don't have to get their own.
            SimulationNowTime = Util.EnvironmentTickCount();

            // Send collision information to the colliding objects. The objects decide if the collision
            //     is 'real' (like linksets don't collide with themselves) and the individual objects
            //     know if the simulator has subscribed to collisions.
            lock (CollisionLock)
            {
                if (collidersCount > 0)
                {
                    lock (PhysObjects)
                    {
                        if (collidersCount > m_collisionArray.Length)
                            collidersCount = m_collisionArray.Length;
                        for (int ii = 0; ii < collidersCount; ii++)
                        {
                            uint cA = m_collisionArray[ii].aID;
                            uint cB = m_collisionArray[ii].bID;
                            Vector3 point = m_collisionArray[ii].point;
                            Vector3 normal = m_collisionArray[ii].normal;
                            float penetration = m_collisionArray[ii].penetration;
                            SendCollision(cA, cB, point, normal, penetration);
                            SendCollision(cB, cA, point, -normal, penetration);
                        }
                    }
                }
            }

            // If any of the objects had updated properties, tell the managed objects about the update
            //     and remember that there was a change so it will be passed to the simulator.
            lock (UpdateLock)
            {
                if (updatedEntityCount > 0)
                {
                    lock (PhysObjects)
                    {
                        for (int ii = 0; ii < updatedEntityCount; ii++)
                        {
                            EntityProperties entprop = m_updateArray[ii];
                            BSPhysObject pobj;
                            if (PhysObjects.TryGetValue(entprop.ID, out pobj))
                            {
                                if (pobj.IsInitialized)
                                    pobj.UpdateProperties(entprop);
                            }
                        }
                    }
                }
            }

            simTime = Util.EnvironmentTickCountSubtract(beforeTime);
            if (PhysicsLogging.Enabled)
            {
                DetailLog("{0},DoPhysicsStep,complete,frame={1}, nTaints={2}, simTime={3}, substeps={4}, updates={5}, colliders={6}, objWColl={7}",
                                        DetailLogZero, m_simulationStep, numTaints, simTime, numSubSteps,
                                        updatedEntityCount, collidersCount, ObjectsWithCollisions.Count);
            }

            // The following causes the unmanaged code to output ALL the values found in ALL the objects in the world.
            // Only enable this in a limited test world with few objects.
            if (m_physicsPhysicalDumpEnabled)
                PE.DumpAllInfo(World);

            // The physics engine returns the number of milliseconds it simulated this call.
            // These are summed and normalized to one second and divided by 1000 to give the reported physics FPS.
            // Multiply by a fixed nominal frame rate to give a rate similar to the simulator (usually 55).
//            m_simulatedTime +=  (float)numSubSteps * m_fixedTimeStep * 1000f * NominalFrameRate;
            m_simulatedTime +=  (float)numSubSteps * m_fixedTimeStep;
        }

        // Called by a BSPhysObject to note that it has changed properties and this information
        //    should be passed up to the simulator at the proper time.
        // Note: this is called by the BSPhysObject from invocation via DoPhysicsStep() above so
        //    this is is under UpdateLock.
        public void PostUpdate(BSPhysObject updatee)
        {
            lock (UpdateLock)
            {
                ObjectsWithUpdates.Add(updatee);
            }
        }

        // The simulator thinks it is physics time so return all the collisions and position
        //     updates that were collected in actual physics simulation.
        private float SendUpdatesToSimulator(float timeStep)
        {
            if (!m_initialized) return 5.0f;

            DetailLog("{0},SendUpdatesToSimulator,collisions={1},updates={2},simedTime={3}",
                BSScene.DetailLogZero, ObjectsWithCollisions.Count, ObjectsWithUpdates.Count, m_simulatedTime);
            // Push the collisions into the simulator.
            lock (CollisionLock)
            {
                if (ObjectsWithCollisions.Count > 0)
                {
                    foreach (BSPhysObject bsp in ObjectsWithCollisions)
                        if (!bsp.SendCollisions())
                        {
                            // If the object is done colliding, see that it's removed from the colliding list
                            ObjectsWithNoMoreCollisions.Add(bsp);
                        }
                }

                // This is a kludge to get avatar movement updates.
                // The simulator expects collisions for avatars even if there are have been no collisions.
                //    The event updates avatar animations and stuff.
                // If you fix avatar animation updates, remove this overhead and let normal collision processing happen.
                // Note that we get a copy of the list to search because SendCollision() can take a while.
                HashSet<BSPhysObject> tempAvatarsInScene;
                lock (AvatarsInSceneLock)
                {
                    tempAvatarsInScene = new HashSet<BSPhysObject>(AvatarsInScene);
                }
                foreach (BSPhysObject actor in tempAvatarsInScene)
                {
                    if (!ObjectsWithCollisions.Contains(actor))   // don't call avatars twice
                        actor.SendCollisions();
                }
                tempAvatarsInScene = null;

                // Objects that are done colliding are removed from the ObjectsWithCollisions list.
                // Not done above because it is inside an iteration of ObjectWithCollisions.
                // This complex collision processing is required to create an empty collision
                //     event call after all real collisions have happened on an object. This allows
                //     the simulator to generate the 'collision end' event.
                if (ObjectsWithNoMoreCollisions.Count > 0)
                {
                    foreach (BSPhysObject po in ObjectsWithNoMoreCollisions)
                        ObjectsWithCollisions.Remove(po);
                    ObjectsWithNoMoreCollisions.Clear();
                }
            }

            // Call the simulator for each object that has physics property updates.
            HashSet<BSPhysObject> updatedObjects = null;
            lock (UpdateLock)
            {
                if (ObjectsWithUpdates.Count > 0)
                {
                    updatedObjects = ObjectsWithUpdates;
                    ObjectsWithUpdates = new HashSet<BSPhysObject>();
                }
            }
            if (updatedObjects != null)
            {
                foreach (BSPhysObject obj in updatedObjects)
                {
                    obj.RequestPhysicsterseUpdate();
                }
                updatedObjects.Clear();
            }

            // Return the framerate simulated to give the above returned results.
            // (Race condition here but this is just bookkeeping so rare mistakes do not merit a lock).
            float simTime = m_simulatedTime / timeStep;
            m_simulatedTime = 0f;
            return simTime;
        }

        // Something has collided
        private void SendCollision(uint localID, uint collidingWith, Vector3 collidePoint, Vector3 collideNormal, float penetration)
        {
            if (localID <= TerrainManager.HighestTerrainID)
            {
                return;         // don't send collisions to the terrain
            }

            BSPhysObject collider;
            // NOTE that PhysObjects was locked before the call to SendCollision().
            if (!PhysObjects.TryGetValue(localID, out collider))
            {
                // If the object that is colliding cannot be found, just ignore the collision.
                DetailLog("{0},BSScene.SendCollision,colliderNotInObjectList,id={1},with={2}", DetailLogZero, localID, collidingWith);
                return;
            }

            // Note: the terrain is not in the physical object list so 'collidee' can be null when Collide() is called.
            BSPhysObject collidee = null;
            PhysObjects.TryGetValue(collidingWith, out collidee);

            // DetailLog("{0},BSScene.SendCollision,collide,id={1},with={2}", DetailLogZero, localID, collidingWith);

            if (collider.IsInitialized)
            {
                if (collider.Collide(collidee, collidePoint, collideNormal, penetration))
                {
                    // If a collision was 'good', remember to send it to the simulator
                    lock (CollisionLock)
                    {
                        ObjectsWithCollisions.Add(collider);
                    }
                }
            }

            return;
        }

        public void BulletSPluginPhysicsThread()
        {
            m_updateWaitEvent = new ManualResetEvent(false);

            while (m_initialized)
            {
                int beginSimulationRealtimeMS = Util.EnvironmentTickCount();

                if (BSParam.Active)
                    DoPhysicsStep(BSParam.PhysicsTimeStep);

                int simulationRealtimeMS = Util.EnvironmentTickCountSubtract(beginSimulationRealtimeMS);
                int simulationTimeVsRealtimeDifferenceMS = ((int)(BSParam.PhysicsTimeStep*1000f)) - simulationRealtimeMS;

                if (simulationTimeVsRealtimeDifferenceMS > 0)
                {
                    // The simulation of the time interval took less than realtime.
                    // Do a wait for the rest of realtime.
                     m_updateWaitEvent.WaitOne(simulationTimeVsRealtimeDifferenceMS);
                    //Thread.Sleep(simulationTimeVsRealtimeDifferenceMS);
                }
                else
                {
                    // The simulation took longer than realtime.
                    // Do some scaling of simulation time.
                    // TODO.
                    DetailLog("{0},BulletSPluginPhysicsThread,longerThanRealtime={1}", BSScene.DetailLogZero, simulationTimeVsRealtimeDifferenceMS);
                }

                Watchdog.UpdateThread();
            }

            Watchdog.RemoveThread();
        }

        #endregion // Simulation

        #region Terrain

        public override void SetTerrain(float[] heightMap) {
            TerrainManager.SetTerrain(heightMap);
        }

        public override void SetWaterLevel(float baseheight)
        {
            SimpleWaterLevel = baseheight;
        }

        public override void DeleteTerrain()
        {
            // m_log.DebugFormat("{0}: DeleteTerrain()", LogHeader);
        }

        #endregion // Terrain

        #region Raycast

        public override bool SupportsRayCast()
        {
            return BSParam.UseBulletRaycast;
        }

        public override bool SupportsRaycastWorldFiltered()
        {
            return BSParam.UseBulletRaycast;
        }


        /// <summary>
        /// Queue a raycast against the physics scene.
        /// The provided callback method will be called when the raycast is complete
        ///
        /// Many physics engines don't support collision testing at the same time as
        /// manipulating the physics scene, so we queue the request up and callback
        /// a custom method when the raycast is complete.
        /// This allows physics engines that give an immediate result to callback immediately
        /// and ones that don't, to callback when it gets a result back.
        ///      public delegate void RayCallback(List<ContactResult> list);
        ///
        /// ODE for example will not allow you to change the scene while collision testing or
        /// it asserts, 'opteration not valid for locked space'.  This includes adding a ray to the scene.
        ///
        /// This is named RayCastWorld to not conflict with modrex's Raycast method.
        /// </summary>
        /// <param name="position">Origin of the ray</param>
        /// <param name="direction">Direction of the ray</param>
        /// <param name="length">Length of ray in meters</param>
        /// <param name="retMethod">Method to call when the raycast is complete</param>
        public override void RaycastWorld(Vector3 position, Vector3 direction, float length, RaycastCallback retMethod)
        {
            if (retMethod != null)
            {
                if (BSParam.UseBulletRaycast)
                {
                    Vector3 posFrom = position;
                    direction.Normalize();
                    Vector3 posTo = direction * length + position;

                    TaintedObject(DetailLogZero, "BSScene.RaycastWorld1", delegate ()
                    {
                        RaycastHit hitInfo = PE.RayTest2(World, posFrom, posTo, 0xffff, 0xffff);
                        retMethod(true, hitInfo.Point, hitInfo.ID, hitInfo.Fraction, hitInfo.Normal);
                    });
                }
                else
                {
                    retMethod(false, Vector3.Zero, 0, 999999999999f, Vector3.Zero);
                }
            }
        }

        public override void RaycastWorld(Vector3 position, Vector3 direction, float length, int count, RayCallback retMethod)
        {
            if (retMethod != null)
            {
                if (BSParam.UseBulletRaycast)
                {
                    List<ContactResult> hitInfo = RaycastWorld(position, direction, length, count);
                    retMethod(hitInfo);
                }
                else
                {
                    retMethod(new List<ContactResult>());
                }
            }
        }

        public override List<ContactResult> RaycastWorld(Vector3 position, Vector3 direction, float length, int count)
        {
            return (List<ContactResult>)RaycastWorld(position, direction, length, count, RayFilterFlags.All);
        }

        public override object RaycastWorld(Vector3 position, Vector3 direction, float length, int count, RayFilterFlags filter)
        {
            List<ContactResult> ret = new List<ContactResult>();
            if (BSParam.UseBulletRaycast)
            {
                uint collisionFilter = 0;
                uint collisionMask = 0;
                if ((filter & RayFilterFlags.land) != 0)
                {
                    collisionFilter |= BulletSimData.CollisionTypeMasks[CollisionType.Terrain].group;
                    collisionMask |= BulletSimData.CollisionTypeMasks[CollisionType.Terrain].mask;
                }
                if ((filter & RayFilterFlags.agent) != 0)
                {
                    collisionFilter |= BulletSimData.CollisionTypeMasks[CollisionType.Avatar].group;
                    collisionMask |= BulletSimData.CollisionTypeMasks[CollisionType.Avatar].mask;
                }
                if ((filter & RayFilterFlags.nonphysical) != 0)
                {
                    collisionFilter |= BulletSimData.CollisionTypeMasks[CollisionType.Static].group;
                    collisionMask |= BulletSimData.CollisionTypeMasks[CollisionType.Static].mask;
                }
                if ((filter & RayFilterFlags.physical) != 0)
                {
                    collisionFilter |= BulletSimData.CollisionTypeMasks[CollisionType.Dynamic].group;
                    collisionMask |= BulletSimData.CollisionTypeMasks[CollisionType.Dynamic].mask;
                }
                // if ((filter & RayFilterFlags.phantom) != 0)
                // {
                //     collisionFilter |= BulletSimData.CollisionTypeMasks[CollisionType.VolumeDetect].group;
                //     collisionMask |= BulletSimData.CollisionTypeMasks[CollisionType.VolumeDetect].mask;
                // }
                if ((filter & RayFilterFlags.volumedtc) != 0)
                {
                    collisionFilter |= BulletSimData.CollisionTypeMasks[CollisionType.VolumeDetect].group;
                    collisionMask |= BulletSimData.CollisionTypeMasks[CollisionType.VolumeDetect].mask;
                }
                DetailLog("{0},RaycastWorld,pos={1},dir={2},len={3},count={4},filter={5},filter={6},mask={7}",
                        DetailLogZero, position, direction, length, count, filter, collisionFilter, collisionMask);
                // NOTE: locking ensures the physics engine is not executing.
                //    The caller might have to wait for the physics engine to finish.
                lock (PhysicsEngineLock)
                {
                    Vector3 posFrom = position;
                    direction.Normalize();
                    Vector3 posTo = direction * length + position;
                    DetailLog("{0},RaycastWorld,RayTest2,from={1},to={2}",
                                DetailLogZero, posFrom, posTo);
                    RaycastHit hitInfo = PE.RayTest2(World, posFrom, posTo, collisionFilter, collisionMask);
                    if (hitInfo.hasHit())
                    {
                        ContactResult result = new ContactResult();
                        result.Pos = hitInfo.Point;
                        result.Normal = hitInfo.Normal;
                        result.ConsumerID = hitInfo.ID;
                        result.Depth = hitInfo.Fraction;
                        ret.Add(result);
                        DetailLog("{0},RaycastWorld,hit,pos={1},norm={2},depth={3},id={4}",
                            DetailLogZero, result.Pos, result.Normal, result.Depth, result.ConsumerID);
                    }
                }
            }
            return ret;
        }

        #endregion Raycast


        public override Dictionary<uint, float> GetTopColliders()
        {
            Dictionary<uint, float> topColliders;

            lock (PhysObjects)
            {
                foreach (KeyValuePair<uint, BSPhysObject> kvp in PhysObjects)
                {
                    kvp.Value.ComputeCollisionScore();
                }

                List<BSPhysObject> orderedPrims = new List<BSPhysObject>(PhysObjects.Values);
                orderedPrims.OrderByDescending(p => p.CollisionScore);
                topColliders = orderedPrims.Take(25).ToDictionary(p => p.LocalID, p => p.CollisionScore);
            }

            return topColliders;
        }

        #region Extensions
        public override object Extension(string pFunct, params object[] pParams)
        {
            DetailLog("{0} BSScene.Extension,op={1}", DetailLogZero, pFunct);
            return base.Extension(pFunct, pParams);
        }
        #endregion // Extensions

        public static string PrimitiveBaseShapeToString(PrimitiveBaseShape pbs)
        {
            float pathShearX = pbs.PathShearX < 128 ? (float)pbs.PathShearX * 0.01f : (float)(pbs.PathShearX - 256) * 0.01f;
            float pathShearY = pbs.PathShearY < 128 ? (float)pbs.PathShearY * 0.01f : (float)(pbs.PathShearY - 256) * 0.01f;
            float pathBegin = (float)pbs.PathBegin * 2.0e-5f;
            float pathEnd = 1.0f - (float)pbs.PathEnd * 2.0e-5f;
            float pathScaleX = (float)(200 - pbs.PathScaleX) * 0.01f;
            float pathScaleY = (float)(200 - pbs.PathScaleY) * 0.01f;
            float pathTaperX = pbs.PathTaperX * 0.01f;
            float pathTaperY = pbs.PathTaperY * 0.01f;

            float profileBegin = (float)pbs.ProfileBegin * 2.0e-5f;
            float profileEnd = 1.0f - (float)pbs.ProfileEnd * 2.0e-5f;
            float profileHollow = (float)pbs.ProfileHollow * 2.0e-5f;
            if (profileHollow > 0.95f)
                profileHollow = 0.95f;

            StringBuilder buff = new StringBuilder();
            buff.Append("shape=");
            buff.Append(((ProfileShape)pbs.ProfileShape).ToString());
            buff.Append(",");
            buff.Append("hollow=");
            buff.Append(((HollowShape)pbs.HollowShape).ToString());
            buff.Append(",");
            buff.Append("pathCurve=");
            buff.Append(((Extrusion)pbs.PathCurve).ToString());
            buff.Append(",");
            buff.Append("profCurve=");
            buff.Append(((Extrusion)pbs.ProfileCurve).ToString());
            buff.Append(",");
            buff.Append("profHollow=");
            buff.Append(profileHollow.ToString());
            buff.Append(",");
            buff.Append("pathBegEnd=");
            buff.Append(pathBegin.ToString());
            buff.Append("/");
            buff.Append(pathEnd.ToString());
            buff.Append(",");
            buff.Append("profileBegEnd=");
            buff.Append(profileBegin.ToString());
            buff.Append("/");
            buff.Append(profileEnd.ToString());
            buff.Append(",");
            buff.Append("scaleXY=");
            buff.Append(pathScaleX.ToString());
            buff.Append("/");
            buff.Append(pathScaleY.ToString());
            buff.Append(",");
            buff.Append("shearXY=");
            buff.Append(pathShearX.ToString());
            buff.Append("/");
            buff.Append(pathShearY.ToString());
            buff.Append(",");
            buff.Append("taperXY=");
            buff.Append(pbs.PathTaperX.ToString());
            buff.Append("/");
            buff.Append(pbs.PathTaperY.ToString());
            buff.Append(",");
            buff.Append("skew=");
            buff.Append(pbs.PathSkew.ToString());
            buff.Append(",");
            buff.Append("twist/Beg=");
            buff.Append(pbs.PathTwist.ToString());
            buff.Append("/");
            buff.Append(pbs.PathTwistBegin.ToString());

            return buff.ToString();
        }

        #region Taints
        // The simulation execution order is:
        // Simulate()
        //    DoOneTimeTaints
        //    TriggerPreStepEvent
        //    DoOneTimeTaints
        //    Step()
        //       ProcessAndSendToSimulatorCollisions
        //       ProcessAndSendToSimulatorPropertyUpdates
        //    TriggerPostStepEvent

        // Calls to the PhysicsActors can't directly call into the physics engine
        //       because it might be busy. We delay changes to a known time.
        // We rely on C#'s closure to save and restore the context for the delegate.
        // NOTE: 'inTaintTime' is no longer used. This entry exists so all the calls don't have to be changed.
        // public void TaintedObject(bool inTaintTime, String pIdent, TaintCallback pCallback)
        // {
        //     TaintedObject(BSScene.DetailLogZero, pIdent, pCallback);
        // }
        // NOTE: 'inTaintTime' is no longer used. This entry exists so all the calls don't have to be changed.
        public void TaintedObject(bool inTaintTime, uint pOriginator, String pIdent, TaintCallback pCallback)
        {
            TaintedObject(m_physicsLoggingEnabled ? pOriginator.ToString() : BSScene.DetailLogZero, pIdent, pCallback);
        }
        public void TaintedObject(uint pOriginator, String pIdent, TaintCallback pCallback)
        {
            TaintedObject(m_physicsLoggingEnabled ? pOriginator.ToString() : BSScene.DetailLogZero, pIdent, pCallback);
        }
        // Sometimes a potentially tainted operation can be used in and out of taint time.
        // This routine executes the command immediately if in taint-time otherwise it is queued.
        public void TaintedObject(string pOriginator, string pIdent, TaintCallback pCallback)
        {
            if (!m_initialized) return;

/* mantis 8397 ??? avoid out of order operations ???

            if (Monitor.TryEnter(PhysicsEngineLock))
            {
                // If we can get exclusive access to the physics engine, just do the operation
                pCallback();
                Monitor.Exit(PhysicsEngineLock);
            }
            else
            {
*/
                // The physics engine is busy, queue the operation
                lock (_taintLock)
                {
                    _taintOperations.Add(new TaintCallbackEntry(pOriginator, pIdent, pCallback));
                }
//            }
        }

        private void TriggerPreStepEvent(float timeStep)
        {
            PreStepAction actions = BeforeStep;
            if (actions != null)
                actions(timeStep);

        }

        private void TriggerPostStepEvent(float timeStep)
        {
            PostStepAction actions = AfterStep;
            if (actions != null)
                actions(timeStep);

        }

        // When someone tries to change a property on a BSPrim or BSCharacter, the object queues
        // a callback into itself to do the actual property change. That callback is called
        // here just before the physics engine is called to step the simulation.
        // Returns the number of taints processed
        // NOTE: Called while PhysicsEngineLock is locked
        public int ProcessTaints()
        {
            int ret = 0;
            ret += ProcessRegularTaints();
            ret += ProcessPostTaintTaints();
            return ret;
        }

        // Returns the number of taints processed
        // NOTE: Called while PhysicsEngineLock is locked
        private int ProcessRegularTaints()
        {
            int ret = 0;
            if (m_initialized && _taintOperations.Count > 0)  // save allocating new list if there is nothing to process
            {
                // swizzle a new list into the list location so we can process what's there
                List<TaintCallbackEntry> oldList;
                lock (_taintLock)
                {
                    oldList = _taintOperations;
                    _taintOperations = new List<TaintCallbackEntry>();
                }

                foreach (TaintCallbackEntry tcbe in oldList)
                {
                    try
                    {
                        DetailLog("{0},BSScene.ProcessTaints,doTaint,id={1}", tcbe.originator, tcbe.ident); // DEBUG DEBUG DEBUG
                        tcbe.callback();
                        ret++;
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("{0}: ProcessTaints: {1}: Exception: {2}", LogHeader, tcbe.ident, e);
                    }
                }
                oldList.Clear();
            }
            return ret;
        }

        // Schedule an update to happen after all the regular taints are processed.
        // Note that new requests for the same operation ("ident") for the same object ("ID")
        //     will replace any previous operation by the same object.
        public void PostTaintObject(String ident, uint ID, TaintCallback callback)
        {
            string IDAsString = ID.ToString();
            string uniqueIdent = ident + "-" + IDAsString;
            lock (_taintLock)
            {
                _postTaintOperations[uniqueIdent] = new TaintCallbackEntry(IDAsString, uniqueIdent, callback);
            }

            return;
        }

        // Taints that happen after the normal taint processing but before the simulation step.
        // Returns the number of taints processed
        // NOTE: Called while PhysicsEngineLock is locked
        private int ProcessPostTaintTaints()
        {
            int ret = 0;
            if (m_initialized && _postTaintOperations.Count > 0)
            {
                Dictionary<string, TaintCallbackEntry> oldList;
                lock (_taintLock)
                {
                    oldList = _postTaintOperations;
                    _postTaintOperations = new Dictionary<string, TaintCallbackEntry>();
                }

                foreach (KeyValuePair<string,TaintCallbackEntry> kvp in oldList)
                {
                    try
                    {
                        DetailLog("{0},BSScene.ProcessPostTaintTaints,doTaint,id={1}", DetailLogZero, kvp.Key); // DEBUG DEBUG DEBUG
                        kvp.Value.callback();
                        ret++;
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("{0}: ProcessPostTaintTaints: {1}: Exception: {2}", LogHeader, kvp.Key, e);
                    }
                }
                oldList.Clear();
            }
            return ret;
        }
        #endregion // Taints

        #region IPhysicsParameters
        // Get the list of parameters this physics engine supports
        public PhysParameterEntry[] GetParameterList()
        {
            BSParam.BuildParameterTable();
            return BSParam.SettableParameters;
        }

        // Set parameter on a specific or all instances.
        // Return 'false' if not able to set the parameter.
        // Setting the value in the m_params block will change the value the physics engine
        //   will use the next time since it's pinned and shared memory.
        // Some of the values require calling into the physics engine to get the new
        //   value activated ('terrainFriction' for instance).
        public bool SetPhysicsParameter(string parm, string val, uint localID)
        {
            bool ret = false;

            BSParam.ParameterDefnBase theParam;
            if (BSParam.TryGetParameter(parm, out theParam))
            {
                // Set the value in the C# code
                theParam.SetValue(this, val);

                // Optionally set the parameter in the unmanaged code
                if (theParam.HasSetOnObject)
                {
                    // update all the localIDs specified
                    // If the local ID is APPLY_TO_NONE, just change the default value
                    // If the localID is APPLY_TO_ALL change the default value and apply the new value to all the lIDs
                    // If the localID is a specific object, apply the parameter change to only that object
                    List<uint> objectIDs = new List<uint>();
                    switch (localID)
                    {
                        case PhysParameterEntry.APPLY_TO_NONE:
                            // This will cause a call into the physical world if some operation is specified (SetOnObject).
                            objectIDs.Add(TERRAIN_ID);
                            TaintedUpdateParameter(parm, objectIDs, val);
                            break;
                        case PhysParameterEntry.APPLY_TO_ALL:
                            lock (PhysObjects) objectIDs = new List<uint>(PhysObjects.Keys);
                            TaintedUpdateParameter(parm, objectIDs, val);
                            break;
                        default:
                            // setting only one localID
                            objectIDs.Add(localID);
                            TaintedUpdateParameter(parm, objectIDs, val);
                            break;
                    }
                }

                ret = true;
            }
            return ret;
        }

        // schedule the actual updating of the paramter to when the phys engine is not busy
        private void TaintedUpdateParameter(string parm, List<uint> lIDs, string val)
        {
            string xval = val;
            List<uint> xlIDs = lIDs;
            string xparm = parm;
            TaintedObject(DetailLogZero, "BSScene.UpdateParameterSet", delegate() {
                BSParam.ParameterDefnBase thisParam;
                if (BSParam.TryGetParameter(xparm, out thisParam))
                {
                    if (thisParam.HasSetOnObject)
                    {
                        foreach (uint lID in xlIDs)
                        {
                            BSPhysObject theObject = null;
                            if (PhysObjects.TryGetValue(lID, out theObject))
                                thisParam.SetOnObject(this, theObject);
                        }
                    }
                }
            });
        }

        // Get parameter.
        // Return 'false' if not able to get the parameter.
        public bool GetPhysicsParameter(string parm, out string value)
        {
            string val = String.Empty;
            bool ret = false;
            BSParam.ParameterDefnBase theParam;
            if (BSParam.TryGetParameter(parm, out theParam))
            {
                val = theParam.GetValue(this);
                ret = true;
            }
            value = val;
            return ret;
        }

        #endregion IPhysicsParameters

        // Invoke the detailed logger and output something if it's enabled.
        public void DetailLog(string msg, params Object[] args)
        {
            PhysicsLogging.Write(msg, args);
        }
        // Used to fill in the LocalID when there isn't one. It's the correct number of characters.
        public const string DetailLogZero = "0000000000";

    }
}
