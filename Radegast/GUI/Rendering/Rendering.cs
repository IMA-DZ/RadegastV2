// 
// Radegast Metaverse Client
// Copyright (c) 2009-2013, Radegast Development Team
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 
//     * Redistributions of source code must retain the above copyright notice,
//       this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright
//       notice, this list of conditions and the following disclaimer in the
//       documentation and/or other materials provided with the distribution.
//     * Neither the name of the application "Radegast", nor the names of its
//       contributors may be used to endorse or promote products derived from
//       this software without specific prior CreateReflectionTexture permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// $Id$
//

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using System.Text;
#if (COGBOT_LIBOMV || USE_STHREADS)
using ThreadPoolUtil;
using Thread = ThreadPoolUtil.Thread;
using ThreadPool = ThreadPoolUtil.ThreadPool;
using Monitor = ThreadPoolUtil.Monitor;
#endif
using System.Threading;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using OpenMetaverse;
using OpenMetaverse.Rendering;
using OpenMetaverse.Assets;
using OpenMetaverse.Imaging;
using OpenMetaverse.StructuredData;
using OpenMetaverse.Packets;
#endregion Usings

namespace Radegast.Rendering
{

    public partial class SceneWindow : RadegastTabControl
    {
        #region Public fields
        /// <summary>
        /// The OpenGL surface
        /// </summary>
        public OpenTK.GLControl glControl = null;

        /// <summary>
        /// Use multi sampling (anti aliasing)
        /// </summary>
        public bool UseMultiSampling = true;

        /// <summary>
        /// Is rendering engine ready and enabled
        /// </summary>
        public bool RenderingEnabled = false;

        /// <summary>
        /// Rednder in wireframe mode
        /// </summary>
        public bool Wireframe = false;

        /// <summary>
        /// Object from up to this distance from us will be rendered
        /// </summary>
        public float DrawDistance
        {
            get { return drawDistance; }
            set
            {
                drawDistance = value;
                drawDistanceSquared = value * value;
                if (Camera != null)
                    Camera.Far = value;
            }
        }

        /// <summary>
        /// List of prims in the scene
        /// </summary>
        Dictionary<uint, RenderPrimitive> Prims = new Dictionary<uint, RenderPrimitive>();
        List<SceneObject> SortedObjects;
        List<SceneObject> OccludedObjects;
        List<RenderAvatar> VisibleAvatars;
        Dictionary<uint, RenderAvatar> Avatars = new Dictionary<uint, RenderAvatar>();

        /// <summary>
        /// Cache images after jpeg2000 decode. Uses a lot of disk space and can cause disk trashing
        /// </summary>
        public bool CacheDecodedTextures = false;

        /// <summary>
        /// Size of OpenGL window we're drawing on
        /// </summary>
        public int[] Viewport = new int[4];
        #endregion Public fields

        #region Private fields

        ChatOverlay chatOverlay;
        TextRendering textRendering;
        Camera Camera;
        SceneObject trackedObject;
        Vector3 lastTrackedObjectPos = RHelp.InvalidPosition;
        RenderAvatar myself;

        Dictionary<UUID, TextureInfo> TexturesPtrMap = new Dictionary<UUID, TextureInfo>();
        MeshmerizerR renderer;
        OpenTK.Graphics.GraphicsMode GLMode = null;
        AutoResetEvent TextureThreadContextReady = new AutoResetEvent(false);

        delegate void GenericTask();
        BlockingQueue<GenericTask> PendingTasks = new BlockingQueue<GenericTask>();
        Thread genericTaskThread;

        BlockingQueue<TextureLoadItem> PendingTextures = new BlockingQueue<TextureLoadItem>();

        Dictionary<UUID, int> AssetFetchFailCount = new Dictionary<UUID, int>();

        Font HoverTextFont = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Regular);
        Font AvatarTagFont = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold);
        Dictionary<UUID, Bitmap> sculptCache = new Dictionary<UUID, Bitmap>();
        OpenTK.Matrix4 ModelMatrix;
        OpenTK.Matrix4 ProjectionMatrix;
        System.Diagnostics.Stopwatch renderTimer;
        float lastFrameTime = 0f;
        float advTimerTick = 0f;
        float minLODFactor = 0.0001f;

        float[] sunPos = new float[] { 128f, 128f, 5000f, 1f };
        float ambient = 0.26f;
        float difuse = 0.27f;
        float specular = 0.20f;
        OpenTK.Vector4 ambientColor;
        OpenTK.Vector4 difuseColor;
        OpenTK.Vector4 specularColor;
        float drawDistance;
        float drawDistanceSquared;

        RenderTerrain terrain;

        GridClient Client;
        RadegastInstance Instance;

        #endregion Private fields

        #region Construction and disposal
        public SceneWindow(RadegastInstance instance)
            : base(instance)
        {
            InitializeComponent();

            this.Instance = instance;
            this.Client = instance.Client;

            UseMultiSampling = Instance.GlobalSettings["use_multi_sampling"];

            genericTaskThread = new Thread(new ThreadStart(GenericTaskRunner));
            genericTaskThread.IsBackground = true;
            genericTaskThread.Name = "Generic task queue";
            genericTaskThread.Start();

            renderer = new MeshmerizerR();
            renderTimer = new System.Diagnostics.Stopwatch();
            renderTimer.Start();

            // Camera initial setting
            Instance.State.CameraTracksOwnAvatar = false;
            Camera = new Camera();
            InitCamera();
            SetWaterPlanes();

            chatOverlay = new ChatOverlay(instance, this);
            textRendering = new TextRendering(instance);
            terrain = new RenderTerrain(instance);

            cbChatType.SelectedIndex = 1;

            DrawDistance = Instance.GlobalSettings["draw_distance"];
            pnlDebug.Visible = Instance.GlobalSettings["scene_viewer_debug_panel"];

            Client.Objects.TerseObjectUpdate += new EventHandler<TerseObjectUpdateEventArgs>(Objects_TerseObjectUpdate);
            Client.Objects.ObjectUpdate += new EventHandler<PrimEventArgs>(Objects_ObjectUpdate);
            Client.Objects.AvatarUpdate += new EventHandler<AvatarUpdateEventArgs>(Objects_AvatarUpdate);

            Client.Network.RegisterCallback(PacketType.KillObject, KillObjectHandler);
            Client.Network.SimChanged += new EventHandler<SimChangedEventArgs>(Network_SimChanged);
            Client.Terrain.LandPatchReceived += new EventHandler<LandPatchReceivedEventArgs>(Terrain_LandPatchReceived);
            Client.Avatars.AvatarAnimation += new EventHandler<AvatarAnimationEventArgs>(AvatarAnimationChanged);
            Client.Avatars.AvatarAppearance += new EventHandler<AvatarAppearanceEventArgs>(Avatars_AvatarAppearance);
            Client.Appearance.AppearanceSet += new EventHandler<AppearanceSetEventArgs>(Appearance_AppearanceSet);
            Instance.Netcom.ClientDisconnected += new EventHandler<DisconnectedEventArgs>(Netcom_ClientDisconnected);
            Application.Idle += new EventHandler(Application_Idle);
        }

        void DisposeInternal()
        {
            RenderingEnabled = false;
            Application.Idle -= new EventHandler(Application_Idle);
            Instance.State.CameraTracksOwnAvatar = true;
            Instance.State.SetDefaultCamera();

            if (!PendingTextures.Closed)
            {
                TextureThreadContextReady.Reset();
                TextureThreadRunning = false;
                PendingTextures.Close();
                TextureThreadContextReady.WaitOne(5000, false);
            }

            if (chatOverlay != null)
            {
                chatOverlay.Dispose();
                chatOverlay = null;
            }

            if (textRendering != null)
            {
                textRendering.Dispose();
                textRendering = null;
            }

            Client.Objects.TerseObjectUpdate -= new EventHandler<TerseObjectUpdateEventArgs>(Objects_TerseObjectUpdate);
            Client.Objects.ObjectUpdate -= new EventHandler<PrimEventArgs>(Objects_ObjectUpdate);
            Client.Objects.AvatarUpdate -= new EventHandler<AvatarUpdateEventArgs>(Objects_AvatarUpdate);
            Client.Network.UnregisterCallback(PacketType.KillObject, KillObjectHandler);
            Client.Network.SimChanged -= new EventHandler<SimChangedEventArgs>(Network_SimChanged);
            Client.Terrain.LandPatchReceived -= new EventHandler<LandPatchReceivedEventArgs>(Terrain_LandPatchReceived);
            Client.Avatars.AvatarAnimation -= new EventHandler<AvatarAnimationEventArgs>(AvatarAnimationChanged);
            Client.Avatars.AvatarAppearance -= new EventHandler<AvatarAppearanceEventArgs>(Avatars_AvatarAppearance);
            Client.Appearance.AppearanceSet -= new EventHandler<AppearanceSetEventArgs>(Appearance_AppearanceSet);

            if (!PendingTasks.Closed)
            {
                PendingTasks.Close();
            }

            if (genericTaskThread != null)
            {
                genericTaskThread.Join(2000);
                genericTaskThread = null;
            }

            if (instance.Netcom != null)
            {
                Instance.Netcom.ClientDisconnected -= new EventHandler<DisconnectedEventArgs>(Netcom_ClientDisconnected);
            }

            lock (sculptCache)
            {
                foreach (var img in sculptCache.Values)
                    img.Dispose();
                sculptCache.Clear();
            }

            lock (Prims) Prims.Clear();
            lock (Avatars) Avatars.Clear();

            TexturesPtrMap.Clear();

            if (glControl != null)
            {
                glControl_UnhookEvents();
                try
                {
                    glControl.MakeCurrent();
                }
                catch { }
                try
                {
                    glControl.Dispose();
                }
                catch { }
            }
            glControl = null;

            GC.Collect();
        }

        void Application_Idle(object sender, EventArgs e)
        {
            if (RenderingEnabled && glControl != null && !glControl.IsDisposed)
            {
                try
                {
                    while (RenderingEnabled && glControl != null && glControl.IsIdle)
                    {
                        MainRenderLoop();
                        if (instance.MonoRuntime)
                        {
                            Application.DoEvents();
                        }
                    }
                }
                catch (ObjectDisposedException)
                { }
#if !DEBUG
                catch (NullReferenceException)
                { }
                catch (Exception ex)
                {
                    RenderingEnabled = false;
                    Logger.Log("Crash of the 3D scene viewer:\n" + ex.ToString(), Helpers.LogLevel.Error);
                    Dispose();
                }
#endif
            }
        }
        #endregion Construction and disposal

        #region Tab Events
        public void RegisterTabEvents()
        {
            this.RadegastTab.TabAttached += new EventHandler(RadegastTab_TabAttached);
            this.RadegastTab.TabDetached += new EventHandler(RadegastTab_TabDetached);
            this.RadegastTab.TabClosed += new EventHandler(RadegastTab_TabClosed);
        }

        public void UnregisterTabEvents()
        {
            this.RadegastTab.TabAttached -= new EventHandler(RadegastTab_TabAttached);
            this.RadegastTab.TabDetached -= new EventHandler(RadegastTab_TabDetached);
            this.RadegastTab.TabClosed -= new EventHandler(RadegastTab_TabClosed);
        }

        void RadegastTab_TabDetached(object sender, EventArgs e)
        {
            Instance.GlobalSettings["scene_window_docked"] = false;
        }

        void RadegastTab_TabAttached(object sender, EventArgs e)
        {
            Instance.GlobalSettings["scene_window_docked"] = true;
        }

        void RadegastTab_TabClosed(object sender, EventArgs e)
        {
            if (this.RadegastTab != null)
            {
                UnregisterTabEvents();
            }
        }

        #endregion Tab Events

        #region Network messaage handlers
        void Terrain_LandPatchReceived(object sender, LandPatchReceivedEventArgs e)
        {
            if (e.Simulator.Handle == Client.Network.CurrentSim.Handle)
            {
                terrain.Modified = true;
            }
        }

        void Netcom_ClientDisconnected(object sender, DisconnectedEventArgs e)
        {
            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new MethodInvoker(() => Dispose()));
                return;
            }
        }

        void Network_SimChanged(object sender, SimChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(() => Network_SimChanged(sender, e)));
                return;
            }

            terrain.ResetTerrain();
            lock (sculptCache)
            {
                foreach (var img in sculptCache.Values)
                    img.Dispose();
                sculptCache.Clear();
            }
            lock (Prims) Prims.Clear();
            lock (Avatars) Avatars.Clear();
            SetWaterPlanes();
            LoadCurrentPrims();
            InitCamera();
        }

        protected void KillObjectHandler(object sender, PacketReceivedEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(() => KillObjectHandler(sender, e)));
                }
                return;
            }

            KillObjectPacket kill = (KillObjectPacket)e.Packet;

            lock (Prims)
            {
                for (int i = 0; i < kill.ObjectData.Length; i++)
                {
                    uint id = kill.ObjectData[i].ID;
                    if (Prims.ContainsKey(id))
                    {
                        Prims[id].Dispose();
                        Prims.Remove(id);
                    }
                }
            }

            lock (Avatars)
            {
                for (int i = 0; i < kill.ObjectData.Length; i++)
                {
                    uint id = kill.ObjectData[i].ID;
                    if (Avatars.ContainsKey(id))
                    {
                        Avatars[id].Dispose();
                        Avatars.Remove(id);
                    }
                }
            }
        }

        void Objects_TerseObjectUpdate(object sender, TerseObjectUpdateEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            if (e.Prim.ID == Client.Self.AgentID)
            {
                trackedObject = myself;
            }

            //If it is an avatar, we don't need to deal with the terse update stuff, unless it sends textures to us
            if (e.Prim.PrimData.PCode == PCode.Avatar && e.Update.Textures == null)
                return;

            UpdatePrimBlocking(e.Prim);
        }

        void Objects_ObjectUpdate(object sender, PrimEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            UpdatePrimBlocking(e.Prim);
        }

        void Objects_AvatarUpdate(object sender, AvatarUpdateEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;
            AddAvatarToScene(e.Avatar);
        }


        // This is called when ever an animation play state changes, that might be a start/stop event etc
        // the entire list of animations is sent each time and it is our job to determine which are new and
        // which are deleted

        void AvatarAnimationChanged(object sender, AvatarAnimationEventArgs e)
        {

            if (InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(() => AvatarAnimationChanged(sender, e)));
                return;
            }

            // We don't currently have UUID -> RenderAvatar mapping so we need to walk the list
            foreach (RenderAvatar av in Avatars.Values)
            {
                if (av.avatar.ID == e.AvatarID)
                {
                    UpdateAvatarAnimations(av);
                    break;
                }
            }
        }

        void animRecievedCallback(AssetDownload transfer, Asset asset)
        {

            if (InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(() => animRecievedCallback(transfer, asset)));
                return;
            }

            if (transfer.Success)
            {
                skeleton.addanimation(asset, transfer.ID, null, asset.AssetID);
            }
            else
            {
                int nofails = 1;
                if (AssetFetchFailCount.TryGetValue(transfer.AssetID, out nofails))
                {
                    nofails++;
                }

                AssetFetchFailCount[transfer.AssetID] = nofails;

            }


        }

        void Avatars_AvatarAppearance(object sender, AvatarAppearanceEventArgs e)
        {
            if (e.Simulator.Handle != Client.Network.CurrentSim.Handle) return;

            Avatar a = e.Simulator.ObjectsAvatars.Find(av => av.ID == e.AvatarID);
            if (a != null)
            {
                AddAvatarToScene(a);
            }
        }

        void Appearance_AppearanceSet(object sender, AppearanceSetEventArgs e)
        {
            if (e.Success)
            {
                Avatar me;
                if (Client.Network.CurrentSim.ObjectsAvatars.TryGetValue(Client.Self.LocalID, out me))
                {
                    AddAvatarToScene(me);
                }
            }
        }

        #endregion Network messaage handlers

        #region glControl setup and disposal
        public void SetupGLControl()
        {
            RenderingEnabled = false;

            if (glControl != null)
                glControl.Dispose();
            glControl = null;

            GLMode = null;

            try
            {
                if (!UseMultiSampling)
                {
                    GLMode = new OpenTK.Graphics.GraphicsMode(OpenTK.DisplayDevice.Default.BitsPerPixel, 24, 8, 0);
                }
                else
                {
                    for (int aa = 0; aa <= 4; aa += 2)
                    {
                        var testMode = new OpenTK.Graphics.GraphicsMode(OpenTK.DisplayDevice.Default.BitsPerPixel, 24, 8, aa);
                        if (testMode.Samples == aa)
                        {
                            GLMode = testMode;
                        }
                    }
                }
            }
            catch
            {
                GLMode = null;
            }


            try
            {
                if (GLMode == null)
                {
                    // Try default mode
                    glControl = new OpenTK.GLControl();
                }
                else
                {
                    glControl = new OpenTK.GLControl(GLMode);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex.Message, Helpers.LogLevel.Warning, Client);
                glControl = null;
            }

            if (glControl == null)
            {
                Logger.Log("Failed to initialize OpenGL control, cannot continue", Helpers.LogLevel.Error, Client);
                return;
            }

            Logger.Log("Initializing OpenGL mode: " + GLMode.ToString(), Helpers.LogLevel.Info);

            glControl.Paint += glControl_Paint;
            glControl.Resize += glControl_Resize;
            glControl.MouseDown += glControl_MouseDown;
            glControl.MouseUp += glControl_MouseUp;
            glControl.MouseMove += glControl_MouseMove;
            glControl.MouseWheel += glControl_MouseWheel;
            glControl.Load += new EventHandler(glControl_Load);
            glControl.Disposed += new EventHandler(glControl_Disposed);
            glControl.Dock = DockStyle.Fill;
            glControl.VSync = false;
            Controls.Add(glControl);
            glControl.BringToFront();
        }

        void glControl_UnhookEvents()
        {
            glControl.Paint -= glControl_Paint;
            glControl.Resize -= glControl_Resize;
            glControl.MouseDown -= glControl_MouseDown;
            glControl.MouseUp -= glControl_MouseUp;
            glControl.MouseMove -= glControl_MouseMove;
            glControl.MouseWheel -= glControl_MouseWheel;
            glControl.Load -= new EventHandler(glControl_Load);
            glControl.Disposed -= glControl_Disposed;

        }
        void glControl_Disposed(object sender, EventArgs e)
        {
            glControl_UnhookEvents();
        }

        void SetSun()
        {
            ambientColor = new OpenTK.Vector4(ambient, ambient, ambient, difuse);
            difuseColor = new OpenTK.Vector4(difuse, difuse, difuse, difuse);
            specularColor = new OpenTK.Vector4(specular, specular, specular, specular);
            GL.Light(LightName.Light0, LightParameter.Ambient, ambientColor);
            GL.Light(LightName.Light0, LightParameter.Diffuse, difuseColor);
            GL.Light(LightName.Light0, LightParameter.Specular, specularColor);
            GL.Light(LightName.Light0, LightParameter.Position, sunPos);
        }

        bool glControlLoaded = false;
        void glControl_Load(object sender, EventArgs e)
        {
            if (glControlLoaded) return;

            try
            {
                GL.ShadeModel(ShadingModel.Smooth);

                GL.Enable(EnableCap.Lighting);
                GL.Enable(EnableCap.Light0);
                SetSun();

                GL.ClearDepth(1.0d);
                GL.Enable(EnableCap.DepthTest);
                GL.Enable(EnableCap.CullFace);
                GL.CullFace(CullFaceMode.Back);

                // GL.Color() tracks objects ambient and diffuse color
                GL.Enable(EnableCap.ColorMaterial);
                GL.ColorMaterial(MaterialFace.Front, ColorMaterialParameter.AmbientAndDiffuse);

                GL.DepthMask(true);
                GL.DepthFunc(DepthFunction.Lequal);
                GL.Hint(HintTarget.PerspectiveCorrectionHint, HintMode.Nicest);
                GL.MatrixMode(MatrixMode.Projection);

                GL.AlphaFunc(AlphaFunction.Greater, 0.5f);
                GL.BlendFunc(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);

                // Compatibility checks
                OpenTK.Graphics.IGraphicsContextInternal context = glControl.Context as OpenTK.Graphics.IGraphicsContextInternal;
                string glExtensions = GL.GetString(StringName.Extensions);

                // VBO
                RenderSettings.ARBVBOPresent = context.GetAddress("glGenBuffersARB") != IntPtr.Zero;
                RenderSettings.CoreVBOPresent = context.GetAddress("glGenBuffers") != IntPtr.Zero;
                RenderSettings.UseVBO = (RenderSettings.ARBVBOPresent || RenderSettings.CoreVBOPresent)
                    && instance.GlobalSettings["rendering_use_vbo"];

                // Occlusion Query
                RenderSettings.ARBQuerySupported = context.GetAddress("glGetQueryObjectivARB") != IntPtr.Zero;
                RenderSettings.CoreQuerySupported = context.GetAddress("glGetQueryObjectiv") != IntPtr.Zero;
                RenderSettings.OcclusionCullingEnabled = (RenderSettings.CoreQuerySupported || RenderSettings.ARBQuerySupported)
                    && instance.GlobalSettings["rendering_occlusion_culling_enabled2"];

                // Mipmap
                RenderSettings.HasMipmap = context.GetAddress("glGenerateMipmap") != IntPtr.Zero;

                // Shader support
                RenderSettings.HasShaders = glExtensions.Contains("vertex_shader") && glExtensions.Contains("fragment_shader");

                // Multi texture
                RenderSettings.HasMultiTexturing = context.GetAddress("glMultiTexCoord2f") != IntPtr.Zero;
                RenderSettings.WaterReflections = instance.GlobalSettings["water_reflections"];

                if (!RenderSettings.HasMultiTexturing || !RenderSettings.HasShaders)
                {
                    RenderSettings.WaterReflections = false;
                }

                // Do textures have to have dimensions that are powers of two
                RenderSettings.TextureNonPowerOfTwoSupported = glExtensions.Contains("texture_non_power_of_two");

                // Occlusion culling
                RenderSettings.OcclusionCullingEnabled = Instance.GlobalSettings["rendering_occlusion_culling_enabled2"]
                    && (RenderSettings.ARBQuerySupported || RenderSettings.CoreQuerySupported);

                // Shiny
                RenderSettings.EnableShiny = Instance.GlobalSettings["scene_viewer_shiny"];

                RenderingEnabled = true;
                // Call the resizing function which sets up the GL drawing window
                // and will also invalidate the GL control
                glControl_Resize(null, null);
                RenderingEnabled = false;

                // glControl.Context.MakeCurrent(null);
                TextureThreadContextReady.Reset();
                var textureThread = new Thread(() => TextureThread())
                {
                    IsBackground = true,
                    Name = "TextureDecodingThread"
                };
                textureThread.Start();
                TextureThreadContextReady.WaitOne(1000, false);
                // glControl.MakeCurrent();
                InitWater();
                InitShaders();
                RenderingEnabled = true;
                glControlLoaded = true;
                LoadCurrentPrims();
            }
            catch (Exception ex)
            {
                RenderingEnabled = false;
                Logger.Log("Failed to initialize OpenGL control", Helpers.LogLevel.Warning, Client, ex);
            }
        }

        ShaderProgram shinyProgram = new ShaderProgram();
        void InitShaders()
        {
            if (RenderSettings.HasShaders)
            {
                shinyProgram.Load("shiny.vert", "shiny.frag");
                shinyProgram.SetUniform1("colorMap", 0);
            }
        }

        #endregion glControl setup and disposal

        #region glControl paint and resize events
        private void MainRenderLoop()
        {
            if (!RenderingEnabled) return;
            lastFrameTime = (float)renderTimer.Elapsed.TotalSeconds;

            // Something went horribly wrong
            if (lastFrameTime < 0) return;

            // Stopwatch loses resolution if it runs for a long time, reset it
            renderTimer.Reset();
            renderTimer.Start();

            // Determine if we need to throttle frame rate
            bool throttle = false;

            // Some other app has focus
            if (Form.ActiveForm == null)
            {
                throttle = true;
            }
            else
            {
                // If we're docked but not active tab, throttle
                if (!this.RadegastTab.Selected && !this.RadegastTab.Detached)
                {
                    throttle = true;
                }
            }

            // Limit FPS to max 15
            if (throttle)
            {
                int msToSleep = 66 - ((int)(lastFrameTime / 1000));
                if (msToSleep < 10) msToSleep = 10;
                Thread.Sleep(msToSleep);
            }

            Render(false);

            glControl.SwapBuffers();
        }

        void glControl_Paint(object sender, EventArgs e)
        {
            MainRenderLoop();
        }

        private void glControl_Resize(object sender, EventArgs e)
        {
            if (!RenderingEnabled) return;
            glControl.MakeCurrent();

            GL.ClearColor(0.39f, 0.58f, 0.93f, 1.0f);

            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            GL.PushMatrix();
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();

            SetPerspective();

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();
        }
        #endregion glControl paint and resize events

        #region Mouse handling
        bool dragging = false;
        int dragX, dragY, downX, downY;

        private void glControl_MouseWheel(object sender, MouseEventArgs e)
        {
            Camera.MoveToTarget(e.Delta / -500f);
        }

        SceneObject RightclickedObject;
        int RightclickedFaceID;
        int LeftclickedFaceID;

        Vector3 RightclickedPosition;

        private void glControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                downX = dragX = e.X;
                downY = dragY = e.Y;

                if (ModifierKeys == Keys.None)
                {
                    object picked;
                    if (TryPick(e.X, e.Y, out picked, out LeftclickedFaceID))
                    {
                        if (picked is RenderPrimitive)
                        {
                            TryTouchObject((RenderPrimitive)picked);
                        }
                    }
                }
                else if (ModifierKeys == Keys.Alt)
                {
                    object picked;
                    int LeftclickedFaceID;
                    Vector3 worldPosition;
                    if (TryPick(e.X, e.Y, out picked, out LeftclickedFaceID, out worldPosition))
                    {
                        trackedObject = null;
                        Camera.FocalPoint = worldPosition;
                        Point screenCenter = new Point(glControl.Width / 2, glControl.Height / 2);
                        Cursor.Position = glControl.PointToScreen(screenCenter);
                        downX = dragX = screenCenter.X;
                        downY = dragY = screenCenter.Y;
                        Cursor.Hide();
                    }
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                object picked;
                RightclickedObject = null;
                if (TryPick(e.X, e.Y, out picked, out RightclickedFaceID, out RightclickedPosition))
                {
                    if (picked is SceneObject)
                    {
                        RightclickedObject = (SceneObject)picked;
                    }
                }
                ctxMenu.Show(glControl, e.X, e.Y);
            }
        }

        private RenderPrimitive m_currentlyTouchingObject = null;
        private void TryTouchObject(RenderPrimitive LeftclickedObject)
        {
            if ((LeftclickedObject.Prim.Flags & PrimFlags.Touch) != 0)
            {
                if (m_currentlyTouchingObject != null)
                {
                    if (m_currentlyTouchingObject.Prim.LocalID != LeftclickedObject.Prim.LocalID)
                    {
                        //Changed what we are touching... stop touching the old one
                        TryEndTouchObject();

                        //Then set the new one and touch it for the first time
                        m_currentlyTouchingObject = LeftclickedObject;
                        Client.Self.Grab(LeftclickedObject.Prim.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, LeftclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                        Client.Self.GrabUpdate(LeftclickedObject.Prim.ID, Vector3.Zero);
                    }
                    else
                        Client.Self.GrabUpdate(LeftclickedObject.Prim.ID, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, LeftclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                }
                else
                {
                    m_currentlyTouchingObject = LeftclickedObject;
                    Client.Self.Grab(LeftclickedObject.Prim.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, LeftclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                    Client.Self.GrabUpdate(LeftclickedObject.Prim.ID, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, LeftclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                }
            }
        }

        private void TryEndTouchObject()
        {
            if (m_currentlyTouchingObject != null)
                Client.Self.DeGrab(m_currentlyTouchingObject.Prim.LocalID, Vector3.Zero, Vector3.Zero, LeftclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
            m_currentlyTouchingObject = null;
        }

        private void glControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                int deltaX = e.X - dragX;
                int deltaY = e.Y - dragY;
                float pixelToM = 1f / 75f;
                if (e.Button == MouseButtons.Left)
                {
                    if (ModifierKeys == Keys.None)
                    {
                        //Only touch if we arn't doing anything else
                        object picked;
                        int LeftclickedFaceID;
                        if (TryPick(e.X, e.Y, out picked, out LeftclickedFaceID))
                        {
                            if (picked is RenderPrimitive)
                                TryTouchObject((RenderPrimitive)picked);
                        }
                        else
                            TryEndTouchObject();
                    }

                    // Pan
                    if (ModifierKeys == Keys.Control || ModifierKeys == (Keys.Alt | Keys.Control | Keys.Shift))
                    {
                        Camera.Pan(deltaX * pixelToM * 2, deltaY * pixelToM * 2);
                    }

                    // Alt-zoom (up down move camera closer to target, left right rotate around target)
                    if (ModifierKeys == Keys.Alt)
                    {
                        Camera.MoveToTarget(deltaY * pixelToM);
                        Camera.Rotate(-deltaX * pixelToM, true);
                    }

                    // Rotate camera in a vertical circle around target on up down mouse movement
                    if (ModifierKeys == (Keys.Alt | Keys.Control))
                    {
                        Camera.Rotate(deltaY * pixelToM, false);
                        Camera.Rotate(-deltaX * pixelToM, true);
                    }
                }

                dragX = e.X;
                dragY = e.Y;
            }
        }

        private void glControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = false;
                Cursor.Show();

                if (ModifierKeys == Keys.None)
                {
                    TryEndTouchObject();//Stop touching no matter whether we are touching anything
                }
            }
        }
        #endregion Mouse handling

        // Switch to ortho display mode for drawing hud
        public void GLHUDBegin()
        {
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -5, 1);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();
        }

        // Switch back to frustrum display mode
        public void GLHUDEnd()
        {
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Lighting);
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
        }

        #region Texture thread
        bool TextureThreadRunning = true;

        void TextureThread()
        {
            //OpenTK.INativeWindow window = new OpenTK.NativeWindow();
            //OpenTK.Graphics.IGraphicsContext context = new OpenTK.Graphics.GraphicsContext(GLMode, window.WindowInfo);
            //context.MakeCurrent(window.WindowInfo);
            TextureThreadContextReady.Set();
            PendingTextures.Open();
            Logger.DebugLog("Started Texture Thread");

            while (/*window.Exists &&*/ TextureThreadRunning)
            {
                //window.ProcessEvents();

                TextureLoadItem item = null;

                if (!PendingTextures.Dequeue(Timeout.Infinite, ref item)) continue;

                // Already have this one loaded
                if (item.Data.TextureInfo.TexturePointer != 0) continue;

                byte[] imageBytes = null;
                if (item.TGAData != null)
                {
                    imageBytes = item.TGAData;
                }
                else if (item.TextureData != null || item.LoadAssetFromCache)
                {
                    if (item.LoadAssetFromCache)
                    {
                        item.TextureData = Client.Assets.Cache.GetCachedAssetBytes(item.Data.TextureInfo.TextureID);
                    }
                    ManagedImage mi;
                    if (!OpenJPEG.DecodeToImage(item.TextureData, out mi)) continue;

                    bool hasAlpha = false;
                    bool fullAlpha = false;
                    bool isMask = false;
                    if ((mi.Channels & ManagedImage.ImageChannels.Alpha) != 0)
                    {
                        fullAlpha = true;
                        isMask = true;

                        // Do we really have alpha, is it all full alpha, or is it a mask
                        for (int i = 0; i < mi.Alpha.Length; i++)
                        {
                            if (mi.Alpha[i] < 255)
                            {
                                hasAlpha = true;
                            }
                            if (mi.Alpha[i] != 0)
                            {
                                fullAlpha = false;
                            }
                            if (mi.Alpha[i] != 0 && mi.Alpha[i] != 255)
                            {
                                isMask = false;
                            }
                        }

                        if (!hasAlpha)
                        {
                            mi.ConvertChannels(mi.Channels & ~ManagedImage.ImageChannels.Alpha);
                        }
                    }

                    item.Data.TextureInfo.HasAlpha = hasAlpha;
                    item.Data.TextureInfo.FullAlpha = fullAlpha;
                    item.Data.TextureInfo.IsMask = isMask;

                    imageBytes = mi.ExportTGA();
                    if (CacheDecodedTextures)
                    {
                        RHelp.SaveCachedImage(imageBytes, item.TeFace.TextureID, hasAlpha, fullAlpha, isMask);
                    }
                }

                if (imageBytes != null)
                {
                    Image img;

                    using (MemoryStream byteData = new MemoryStream(imageBytes))
                    {
                        img = OpenMetaverse.Imaging.LoadTGAClass.LoadTGA(byteData);
                    }

                    Bitmap bitmap = (Bitmap)img;

                    bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);

                    instance.MainForm.BeginInvoke(new MethodInvoker(() =>
                    {
                        item.Data.TextureInfo.TexturePointer = RHelp.GLLoadImage(bitmap, item.Data.TextureInfo.HasAlpha);
                        // GL.Flush();
                        bitmap.Dispose();
                    }));
                }

                item.TextureData = null;
                item.TGAData = null;
                imageBytes = null;
            }
            //context.MakeCurrent(window.WindowInfo);
            //context.Dispose();
            //window.Dispose();
            TextureThreadContextReady.Set();
            Logger.DebugLog("Texture thread exited");
        }
        #endregion Texture thread

        void GenericTaskRunner()
        {
            PendingTasks.Open();
            Logger.DebugLog("Started generic task thread");

            while (true)
            {
                GenericTask task = null;
                if (!PendingTasks.Dequeue(Timeout.Infinite, ref task)) break;
                task.Invoke();
            }
            Logger.DebugLog("Generic task thread exited");
        }

        void LoadCurrentPrims()
        {
            if (!Client.Network.Connected) return;

            WorkPool.QueueUserWorkItem(sync =>
            {
                if (RenderSettings.PrimitiveRenderingEnabled)
                {
                    List<Primitive> mainPrims = Client.Network.CurrentSim.ObjectsPrimitives.FindAll((Primitive root) => root.ParentID == 0);
                    foreach (Primitive mainPrim in mainPrims)
                    {
                        UpdatePrimBlocking(mainPrim);
                        Client.Network.CurrentSim.ObjectsPrimitives
                            .FindAll((Primitive child) => child.ParentID == mainPrim.LocalID)
                            .ForEach((Primitive subPrim) => UpdatePrimBlocking(subPrim));
                    }
                }

                if (RenderSettings.AvatarRenderingEnabled)
                {
                    List<Avatar> avis = Client.Network.CurrentSim.ObjectsAvatars.FindAll((Avatar a) => true);
                    foreach (Avatar avatar in avis)
                    {
                        UpdatePrimBlocking(avatar);
                        Client.Network.CurrentSim.ObjectsPrimitives
                            .FindAll((Primitive child) => child.ParentID == avatar.LocalID)
                            .ForEach((Primitive attachedPrim) =>
                            {
                                UpdatePrimBlocking(attachedPrim);
                                Client.Network.CurrentSim.ObjectsPrimitives
                                    .FindAll((Primitive child) => child.ParentID == attachedPrim.LocalID)
                                    .ForEach((Primitive attachedPrimChild) =>
                                    {
                                        UpdatePrimBlocking(attachedPrimChild);
                                    });
                            });
                    }
                }
            });
        }

        private void ControlLoaded(object sender, EventArgs e)
        {
            WorkPool.QueueUserWorkItem(sync =>
            {
                InitAvatarData();
                AvatarDataInitialzied();
            });
        }

        #region Private methods (the meat)

        private void AvatarDataInitialzied()
        {
            if (IsDisposed) return;

            // Ensure that this is done on the main thread
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(() => AvatarDataInitialzied()));
                return;
            }

            //FIX ME
            //foreach (VisualParamEx vpe in VisualParamEx.morphParams.Values)
            //{
            //    comboBox_morph.Items.Add(vpe.Name);
            //}

            //foreach (VisualParamEx vpe in VisualParamEx.drivenParams.Values)
            //{
            //    comboBox_driver.Items.Add(vpe.Name);
            //}

            SetupGLControl();
        }

        private void InitAvatarData()
        {
            GLAvatar.loadlindenmeshes2("avatar_lad.xml");
        }

        public void UpdateCamera()
        {
            if (Client != null)
            {
                Client.Self.Movement.Camera.LookAt(Camera.Position, Camera.FocalPoint);
                Client.Self.Movement.Camera.Far = 4 * (Camera.Far = DrawDistance);
            }
        }

        void InitCamera()
        {
            Vector3 camPos = Client.Self.SimPosition + new Vector3(-4, 0, 1) * Client.Self.Movement.BodyRotation;
            Camera.Position = camPos;
            Camera.FocalPoint = Client.Self.SimPosition + new Vector3(5, 0, 0) * Client.Self.Movement.BodyRotation;
            Camera.Zoom = 1.0f;
            Camera.Far = DrawDistance;
        }

        Vector3 PrimPos(Primitive prim)
        {
            Vector3 pos;
            Quaternion rot;
            PrimPosAndRot(GetSceneObject(prim.LocalID), out pos, out rot);
            return pos;
        }

        /// <summary>
        /// Gets attachment state of a prim
        /// </summary>
        /// <param name="parentLocalID">Prim's parent id</param>
        /// <returns>True, if prim is part of an attachment</returns>
        bool IsAttached(uint parentLocalID)
        {
            if (parentLocalID == 0) return false;
            try
            {
                if (Client.Network.CurrentSim.ObjectsAvatars.ContainsKey(parentLocalID))
                {
                    return true;
                }
                else if (Client.Network.CurrentSim.ObjectsPrimitives.ContainsKey(parentLocalID))
                {
                    return IsAttached(Client.Network.CurrentSim.ObjectsPrimitives[parentLocalID].ParentID);
                }
            }
            catch { }
            return false;
        }

        SceneObject GetSceneObject(uint localID)
        {
            RenderPrimitive parent;
            RenderAvatar avi;
            if (Prims.TryGetValue(localID, out parent))
            {
                return parent;
            }
            else if (Avatars.TryGetValue(localID, out avi))
            {
                return avi;
            }
            return null;
        }

        /// <summary>
        /// Calculates finar rendering position for objects on the scene
        /// </summary>
        /// <param name="obj">SceneObject whose position is calculated</param>
        /// <param name="pos">Rendering position</param>
        /// <param name="rot">Rendering rotation</param>
        void PrimPosAndRot(SceneObject obj, out Vector3 pos, out Quaternion rot)
        {
            // Sanity check
            if (obj == null)
            {
                pos = RHelp.InvalidPosition;
                rot = Quaternion.Identity;
                return;
            }

            if (obj.BasePrim.ParentID == 0)
            {
                // We are the root prim, return our interpolated position
                pos = obj.InterpolatedPosition;
                rot = obj.InterpolatedRotation;
                return;
            }
            else
            {
                pos = RHelp.InvalidPosition;
                rot = Quaternion.Identity;

                // Not root, find our parent
                SceneObject p = GetSceneObject(obj.BasePrim.ParentID);
                if (p == null) return;

                // If we don't know parent position, recursively find out
                if (!p.PositionCalculated)
                {
                    PrimPosAndRot(p, out p.RenderPosition, out p.RenderRotation);
                    p.DistanceSquared = Vector3.DistanceSquared(Camera.RenderPosition, p.RenderPosition);
                    p.PositionCalculated = true;
                }

                Vector3 parentPos = p.RenderPosition;
                Quaternion parentRot = p.RenderRotation;

                if (p is RenderPrimitive)
                {
                    // Child prim (our parent is another prim here)
                    pos = parentPos + obj.InterpolatedPosition * parentRot;
                    rot = parentRot * obj.InterpolatedRotation;
                }
                else if (p is RenderAvatar)
                {
                    // Calculating position and rotation of the root prim of an attachment here
                    // (our parent is an avatar here)
                    RenderAvatar parentav = (RenderAvatar)p;

                    // Check for invalid attachment point
                    int attachment_index = (int)obj.BasePrim.PrimData.AttachmentPoint;
                    if (attachment_index >= GLAvatar.attachment_points.Count()) return;
                    attachment_point apoint = GLAvatar.attachment_points[attachment_index];
                    skeleton skel = parentav.glavatar.skel;
                    if (!skel.mBones.ContainsKey(apoint.joint)) return;

                    // Bone position and rotation
                    Bone bone = skel.mBones[apoint.joint];
                    Vector3 bpos = bone.getTotalOffset();
                    Quaternion brot = bone.getTotalRotation();

                    // Start with avatar positon
                    pos = parentPos;
                    rot = parentRot;

                    // Move by pelvis offset
                    // FIXME 2 dictionay lookups via string key in render loop!
                    // pos -= (parentav.glavatar.skel.mBones["mPelvis"].animation_offset * parentav.RenderRotation) + parentav.glavatar.skel.getOffset("mPelvis") * rot;
                    //pos -= parentav.glavatar.skel.getOffset("mPelvis") * rot;
                    //rot = parentav.glavatar.skel.getRotation("mPelvis") * rot;
                    pos = parentav.AdjustedPosition(pos);
                    // Translate and rotate to the joint calculated position
                    pos += bpos * rot;
                    rot *= brot;

                    // Translate and rotate built in joint offset
                    pos += apoint.position * rot;
                    rot *= apoint.rotation;

                    // Translate and rotate from the offset from the attachment point
                    // set in teh appearance editor
                    pos += obj.BasePrim.Position * rot;
                    rot *= obj.BasePrim.Rotation;

                }
                return;
            }
        }

        /// <summary>
        /// Finds the closest distance between the given pos and an object
        /// (Assumes that the object is a box slightly)
        /// </summary>
        /// <param name="vector3"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        private float FindClosestDistanceSquared(Vector3 calcPos, SceneObject p)
        {
            if (p.BoundingVolume == null
                || !RenderSettings.HeavierDistanceChecking
                || p.BoundingVolume.ScaledR < 10f
                )
                return Vector3.DistanceSquared(calcPos, p.RenderPosition);

            Vector3 posToCheckFrom = Vector3.Zero;
            //Get the bounding boxes for this prim
            Vector3 boundingBoxMin = p.RenderPosition + p.BoundingVolume.ScaledMin;
            Vector3 boundingBoxMax = p.RenderPosition + p.BoundingVolume.ScaledMax;
            posToCheckFrom.X = (calcPos.X < boundingBoxMin.X) ? boundingBoxMin.X : (calcPos.X > boundingBoxMax.X) ? boundingBoxMax.X : calcPos.X;
            posToCheckFrom.Y = (calcPos.Y < boundingBoxMin.Y) ? boundingBoxMin.Y : (calcPos.Y > boundingBoxMax.Y) ? boundingBoxMax.Y : calcPos.Y;
            posToCheckFrom.Z = (calcPos.Z < boundingBoxMin.Z) ? boundingBoxMin.Z : (calcPos.Z > boundingBoxMax.Z) ? boundingBoxMax.Z : calcPos.Z;
            return Vector3.DistanceSquared(calcPos, posToCheckFrom);
        }

        private float Abs(float p)
        {
            if (p < 0)
                p *= -1;
            return p;
        }

        private void SetPerspective()
        {
            float dAspRat = (float)glControl.Width / (float)glControl.Height;
            GluPerspective(50.0f * Camera.Zoom, dAspRat, 0.1f, 1000f);
        }


        private void RenderStats()
        {
            // This is a FIR filter known as a MMA or Modified Mean Average, using a 20 point sampling width
            advTimerTick = ((19 * advTimerTick) + lastFrameTime) / 20;
            // Stats in window title for now
            Text = String.Format("Scene Viewer: FPS {0:000.00} Texture decode queue: {1}, Sculpt queue: {2}",
                1d / advTimerTick,
                PendingTextures.Count,
                PendingTasks.Count);

#if TURNS_OUT_PRINTER_IS_EXPENISVE
            int posX = glControl.Width - 100;
            int posY = 0;

            Printer.Begin();
            Printer.Print(String.Format("FPS {0:000.00}", 1d / advTimerTick), AvatarTagFont, Color.Orange,
                new RectangleF(posX, posY, 100, 50),
                OpenTK.Graphics.TextPrinterOptions.Default, OpenTK.Graphics.TextAlignment.Center);
            Printer.End();
#endif
        }

        private void RenderText(RenderPass pass)
        {
            lock (VisibleAvatars)
            {
                foreach (RenderAvatar av in VisibleAvatars)
                {
                    Vector3 avPos = av.RenderPosition;
                    if (av.DistanceSquared > 400f) continue;

                    byte[] faceColor = null;

                    OpenTK.Vector3 tagPos = RHelp.TKVector3(avPos);
                    tagPos.Z += 1.2f;
                    OpenTK.Vector3 screenPos;
                    if (!Math3D.GluProject(tagPos, ModelMatrix, ProjectionMatrix, Viewport, out screenPos)) continue;

                    string tagText = instance.Names.Get(av.avatar.ID, av.avatar.Name);
                    if (!string.IsNullOrEmpty(av.avatar.GroupName))
                        tagText = av.avatar.GroupName + "\n" + tagText;

                    TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top;
                    var tSize = TextRendering.Measure(tagText, AvatarTagFont, flags);

                    if (pass == RenderPass.Picking)
                    {
                        // Send avatar anyway, we're attached to it
                        int faceID = 0;
                        foreach (FaceData f in av.data)
                        {
                            if (f != null)
                            {
                                byte[] primNrBytes = Utils.Int16ToBytes((short)f.PickingID);
                                faceColor = new byte[] { primNrBytes[0], primNrBytes[1], (byte)faceID, 254 };
                                GL.Color4(faceColor);
                                break;
                            }
                            faceID++;
                        }
                    }

                    OpenTK.Vector3 quadPos = screenPos;
                    screenPos.Y = glControl.Height - screenPos.Y;
                    screenPos.X -= tSize.Width / 2;
                    screenPos.Y -= tSize.Height / 2 + 2;

                    if (screenPos.Y > 0)
                    {
                        // Render tag backround
                        float halfWidth = tSize.Width / 2 + 12;
                        float halfHeight = tSize.Height / 2 + 5;
                        GL.Color4(0f, 0f, 0f, 0.4f);
                        RHelp.Draw2DBox(quadPos.X - halfWidth, quadPos.Y - halfHeight, halfWidth * 2, halfHeight * 2, screenPos.Z);

                        if (pass == RenderPass.Simple)
                        {
                            textRendering.Begin();
                            Color textColor = pass == RenderPass.Simple ?
                                Color.Orange :
                                Color.FromArgb(faceColor[3], faceColor[0], faceColor[1], faceColor[2]);
                            textRendering.Print(tagText, AvatarTagFont, textColor,
                                new Rectangle((int)screenPos.X, (int)screenPos.Y, tSize.Width + 2, tSize.Height + 2),
                                flags);
                            textRendering.End();
                        }
                    }
                }
            }

            lock (SortedObjects)
            {
                int primNr = 0;
                foreach (SceneObject obj in SortedObjects)
                {
                    if (!(obj is RenderPrimitive)) continue;

                    RenderPrimitive prim = (RenderPrimitive)obj;
                    primNr++;

                    if (!string.IsNullOrEmpty(prim.BasePrim.Text))
                    {
                        string text = System.Text.RegularExpressions.Regex.Replace(prim.BasePrim.Text, "(\r?\n)+", "\n");
                        OpenTK.Vector3 primPos = RHelp.TKVector3(prim.RenderPosition);

                        // Display hovertext only on objects that are withing 12m of the camera
                        if (prim.DistanceSquared > (12 * 12)) continue;

                        primPos.Z += prim.BasePrim.Scale.Z * 0.8f;

                        // Convert objects world position to 2D screen position in pixels
                        OpenTK.Vector3 screenPos;
                        if (!Math3D.GluProject(primPos, ModelMatrix, ProjectionMatrix, Viewport, out screenPos)) continue;
                        screenPos.Y = glControl.Height - screenPos.Y;

                        textRendering.Begin();

                        Color color = Color.FromArgb((int)(prim.BasePrim.TextColor.A * 255), (int)(prim.BasePrim.TextColor.R * 255), (int)(prim.BasePrim.TextColor.G * 255), (int)(prim.BasePrim.TextColor.B * 255));

                        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top;
                        var size = TextRendering.Measure(text, HoverTextFont, flags);
                        screenPos.X -= size.Width / 2;
                        screenPos.Y -= size.Height;

                        if (screenPos.Y + size.Height > 0)
                        {
                            if (pass == RenderPass.Picking)
                            {
                                //Send the prim anyway, we're attached to it
                                int faceID = 0;
                                foreach (Face f in prim.Faces)
                                {
                                    if (f.UserData != null)
                                    {
                                        byte[] primNrBytes = Utils.Int16ToBytes((short)((FaceData)f.UserData).PickingID);
                                        byte[] faceColor = new byte[] { primNrBytes[0], primNrBytes[1], (byte)faceID, 255 };
                                        textRendering.Print(text, HoverTextFont, Color.FromArgb(faceColor[3], faceColor[0], faceColor[1], faceColor[2]), new Rectangle((int)screenPos.X, (int)screenPos.Y, size.Width + 2, size.Height + 2), flags);
                                        break;
                                    }
                                    faceID++;
                                }
                            }
                            else
                            {
                                // Shadow
                                if (color != Color.Black)
                                    textRendering.Print(text, HoverTextFont, Color.Black, new Rectangle((int)screenPos.X + 1, (int)screenPos.Y + 1, size.Width + 2, size.Height + 2), flags);

                                // Text
                                textRendering.Print(text, HoverTextFont, color, new Rectangle((int)screenPos.X, (int)screenPos.Y, size.Width + 2, size.Height + 2), flags);
                            }
                        }

                        textRendering.End();
                    }
                }
            }
        }

        #region avatars

        private void AddAvatarToScene(Avatar av)
        {
            lock (Avatars)
            {
                if (Avatars.ContainsKey(av.LocalID))
                {
                    // flag we got an update??
                    updateAVtes(Avatars[av.LocalID]);
                    Avatars[av.LocalID].glavatar.morph(av);
                    UpdateAvatarAnimations(Avatars[av.LocalID]);
                }
                else
                {
                    GLAvatar ga = new GLAvatar();

                    //ga.morph(av);
                    RenderAvatar ra = new Rendering.RenderAvatar();
                    ra.avatar = av;
                    ra.glavatar = ga;
                    updateAVtes(ra);
                    Avatars.Add(av.LocalID, ra);
                    ra.glavatar.morph(av);

                    if (av.LocalID == Client.Self.LocalID)
                    {
                        myself = ra;
                    }

                    UpdateAvatarAnimations(ra);
                }
            }
        }

        private void UpdateAvatarAnimations(RenderAvatar av)
        {
            if (av.avatar.Animations == null) return;

            av.glavatar.skel.flushanimations();
            foreach (Animation anim in av.avatar.Animations)
            {
                //Console.WriteLine(string.Format("AvatarAnimationChanged {0} {1}", anim.AnimationID, anim.AnimationSequence));

                // Don't play internal turn 180 animations
                if (anim.AnimationID == new UUID("038fcec9-5ebd-8a8e-0e2e-6e71a0a1ac53"))
                    continue;

                if (anim.AnimationID == new UUID("6883a61a-b27b-5914-a61e-dda118a9ee2c"))
                    continue;

                av.glavatar.skel.processAnimation(anim.AnimationID);

                int nofails = 0;
                if (AssetFetchFailCount.TryGetValue(anim.AnimationID, out nofails))
                {
                    if (nofails >= 5)
                        continue; // asset fetch has failed 5 times, give up.
                }

                UUID tid = UUID.Random();
                skeleton.mAnimationTransactions.Add(tid, av);

                BinBVHAnimationReader bvh;
                if (skeleton.mAnimationCache.TryGetValue(anim.AnimationID, out bvh))
                {
                    skeleton.addanimation(null, tid, bvh, anim.AnimationID);
                    continue;
                }

                Logger.Log("Requesting new animation asset " + anim.AnimationID.ToString(), Helpers.LogLevel.Info);

                Client.Assets.RequestAsset(anim.AnimationID, AssetType.Animation, false, SourceType.Asset, tid, animRecievedCallback);
            }

            av.glavatar.skel.flushanimationsfinal();
            skeleton.recalcpriorities(av);

        }

        private void updateAVtes(RenderAvatar ra)
        {
            if (ra.avatar.Textures == null)
                return;


            foreach (int fi in RenderAvatar.BakedTextures.Keys)
            {
                Primitive.TextureEntryFace TEF = ra.avatar.Textures.FaceTextures[fi];
                if (TEF == null)
                    continue;

                if (ra.data[fi] == null || ra.data[fi].TextureInfo.TextureID != TEF.TextureID || ra.data[fi].TextureInfo.TexturePointer < 1)
                {
                    FaceData data = new FaceData();
                    ra.data[fi] = data;
                    data.TextureInfo.TextureID = TEF.TextureID;

                    ImageType type = ImageType.Baked;
                    if (ra.avatar.COFVersion > 0) // This avatar was server baked
                    {
                        type = ImageType.ServerBaked;
                    }

                    DownloadTexture(new TextureLoadItem()
                    {
                        Data = data,
                        Prim = ra.avatar,
                        TeFace = ra.avatar.Textures.FaceTextures[fi],
                        ImageType = type,
                        BakeName = RenderAvatar.BakedTextures[fi],
                        AvatarID = ra.avatar.ID
                    }, true);
                }
            }
        }

        private void RenderAvatarsSkeleton(RenderPass pass)
        {
            if (!RenderSettings.RenderAvatarSkeleton) return;

            lock (Avatars)
            {
                foreach (RenderAvatar av in Avatars.Values)
                {
                    // Individual prim matrix
                    GL.PushMatrix();

                    // Prim roation and position
                    //Vector3 pos = av.avatar.Position;

                    Vector3 avataroffset = av.glavatar.skel.getOffset("mPelvis");
                    avataroffset.X += 1.0f;

                    GL.MultMatrix(Math3D.CreateSRTMatrix(Vector3.One, av.RenderRotation, av.RenderPosition - avataroffset * av.RenderRotation));

                    GL.Begin(BeginMode.Lines);

                    GL.Color3(1.0, 0.0, 0.0);

                    foreach (Bone b in av.glavatar.skel.mBones.Values)
                    {
                        Vector3 newpos = b.getTotalOffset();

                        if (b.parent != null)
                        {
                            Vector3 parentpos = b.parent.getTotalOffset();
                            GL.Vertex3(parentpos.X, parentpos.Y, parentpos.Z);
                        }
                        else
                        {
                            GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        }

                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        //Mark the joints


                        newpos.X += 0.01f;
                        newpos.Y += 0.01f;
                        newpos.Z += 0.01f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Z -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X -= 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.X += 0.02f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);

                        newpos.Y -= 0.01f;
                        newpos.Z += 0.01f;
                        newpos.X -= 0.01f;
                        GL.Vertex3(newpos.X, newpos.Y, newpos.Z);



                    }



                    GL.Color3(0.0, 1.0, 0.0);

                    GL.End();

                    GL.PopMatrix();
                }
            }
        }

        private void RenderAvatars(RenderPass pass)
        {
            if (!RenderSettings.AvatarRenderingEnabled) return;

            lock (Avatars)
            {
                GL.EnableClientState(ArrayCap.VertexArray);
                GL.EnableClientState(ArrayCap.TextureCoordArray);
                GL.EnableClientState(ArrayCap.NormalArray);

                int avatarNr = 0;
                foreach (RenderAvatar av in VisibleAvatars)
                {
                    avatarNr++;

                    // Whole avatar position
                    GL.PushMatrix();

                    // Prim roation and position
                    av.UpdateSize();
                    GL.MultMatrix(Math3D.CreateSRTMatrix(Vector3.One, av.RenderRotation, av.AdjustedPosition(av.RenderPosition)));

                    if (av.glavatar._meshes.Count > 0)
                    {
                        int faceNr = 0;
                        foreach (GLMesh mesh in av.glavatar._meshes.Values)
                        {
                            if (av.glavatar.skel.mNeedsMeshRebuild)
                            {
                                mesh.applyjointweights();
                            }

                            faceNr++;
                            if (!av.glavatar._showSkirt && mesh.Name == "skirtMesh")
                                continue;


                            // If we don't have a hair bake OR the hair bake is invisible don't render it
                            if (mesh.Name == "hairMesh" && (av.data[(int)AvatarTextureIndex.HairBaked] == null || av.data[(int)AvatarTextureIndex.HairBaked].TextureInfo.IsInvisible))
                                continue;

                            GL.Color3(1f, 1f, 1f);

                            if (pass == RenderPass.Picking)
                            {
                                GL.Disable(EnableCap.Texture2D);

                                for (int i = 0; i < av.data.Length; i++)
                                {
                                    if (av.data[i] != null)
                                    {
                                        av.data[i].PickingID = avatarNr;
                                    }
                                }
                                byte[] primNrBytes = Utils.Int16ToBytes((short)avatarNr);
                                byte[] faceColor = new byte[] { primNrBytes[0], primNrBytes[1], (byte)faceNr, 254 };
                                GL.Color4(faceColor);
                            }
                            else
                            {
                                if (av.data[mesh.teFaceID] == null)
                                {
                                    GL.Disable(EnableCap.Texture2D);
                                }
                                else
                                {
                                    if (mesh.teFaceID != 0)
                                    {
                                        GL.Enable(EnableCap.Texture2D);
                                        GL.BindTexture(TextureTarget.Texture2D, av.data[mesh.teFaceID].TextureInfo.TexturePointer);
                                    }
                                    else
                                    {
                                        GL.Disable(EnableCap.Texture2D);
                                    }
                                }
                            }

                            GL.TexCoordPointer(2, TexCoordPointerType.Float, 0, mesh.RenderData.TexCoords);
                            GL.VertexPointer(3, VertexPointerType.Float, 0, mesh.RenderData.Vertices);
                            GL.NormalPointer(NormalPointerType.Float, 0, mesh.MorphRenderData.Normals);

                            GL.DrawElements(BeginMode.Triangles, mesh.RenderData.Indices.Length, DrawElementsType.UnsignedShort, mesh.RenderData.Indices);

                            GL.BindTexture(TextureTarget.Texture2D, 0);

                        }

                        av.glavatar.skel.mNeedsMeshRebuild = false;
                    }

                    // Whole avatar position
                    GL.PopMatrix();
                }

                GL.Disable(EnableCap.Texture2D);
                GL.DisableClientState(ArrayCap.NormalArray);
                GL.DisableClientState(ArrayCap.VertexArray);
                GL.DisableClientState(ArrayCap.TextureCoordArray);

            }
        }
        #endregion avatars

        #region Keyboard
        private float upKeyHeld = 0;
        private bool isHoldingHome = false;
        ///<summary>
        ///The time before we fly instead of trying to jump (in seconds)
        ///</summary>
        private const float upKeyHeldBeforeFly = 0.5f;

        void CheckKeyboard(float time)
        {
            if (ModifierKeys == Keys.None)
            {
                // Movement forwards and backwards and body rotation
                Client.Self.Movement.AtPos = Instance.Keyboard.IsKeyDown(Keys.Up);
                Client.Self.Movement.AtNeg = Instance.Keyboard.IsKeyDown(Keys.Down);
                Client.Self.Movement.TurnLeft = Instance.Keyboard.IsKeyDown(Keys.Left);
                Client.Self.Movement.TurnRight = Instance.Keyboard.IsKeyDown(Keys.Right);

                if (Client.Self.Movement.Fly)
                {
                    //Find whether we are going up or down
                    Client.Self.Movement.UpPos = Instance.Keyboard.IsKeyDown(Keys.PageUp);
                    Client.Self.Movement.UpNeg = Instance.Keyboard.IsKeyDown(Keys.PageDown);
                    //The nudge positions are required to land (at least Neg is, unclear whether we should send Pos)
                    Client.Self.Movement.NudgeUpPos = Client.Self.Movement.UpPos;
                    Client.Self.Movement.NudgeUpNeg = Client.Self.Movement.UpNeg;
                    if (Client.Self.Velocity.Z > 0 && Client.Self.Movement.UpNeg)//HACK: Sometimes, stop fly fails
                        Client.Self.Fly(false);//We've hit something, stop flying
                }
                else
                {
                    //Don't send the nudge pos flags, we don't need them
                    Client.Self.Movement.NudgeUpPos = false;
                    Client.Self.Movement.NudgeUpNeg = false;
                    Client.Self.Movement.UpPos = Instance.Keyboard.IsKeyDown(Keys.PageUp);
                    Client.Self.Movement.UpNeg = Instance.Keyboard.IsKeyDown(Keys.PageDown);
                }
                if (Instance.Keyboard.IsKeyDown(Keys.Home))//Flip fly settings
                {
                    //Holding the home key only makes it change once, 
                    // not flip over and over, so keep track of it
                    if (!isHoldingHome)
                    {
                        Client.Self.Movement.Fly = !Client.Self.Movement.Fly;
                        isHoldingHome = true;
                    }
                }
                else
                    isHoldingHome = false;

                if (!Client.Self.Movement.Fly &&
                    Instance.Keyboard.IsKeyDown(Keys.PageUp))
                {
                    upKeyHeld += time;
                    if (upKeyHeld > upKeyHeldBeforeFly)//Wait for a bit before we fly, they may be trying to jump
                        Client.Self.Movement.Fly = true;
                }
                else
                    upKeyHeld = 0;//Reset the count


                if (Client.Self.Movement.TurnLeft)
                {
                    Client.Self.Movement.BodyRotation = Client.Self.Movement.BodyRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, time);
                }
                else if (client.Self.Movement.TurnRight)
                {
                    Client.Self.Movement.BodyRotation = Client.Self.Movement.BodyRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -time);
                }

                if (Instance.Keyboard.IsKeyDown(Keys.Escape))
                {
                    InitCamera();
                    Camera.Manual = false;
                    trackedObject = myself;
                }
            }
            else if (ModifierKeys == Keys.Shift)
            {
                // Strafe
                Client.Self.Movement.LeftNeg = Instance.Keyboard.IsKeyDown(Keys.Right);
                Client.Self.Movement.LeftPos = Instance.Keyboard.IsKeyDown(Keys.Left);
            }
            else if (ModifierKeys == Keys.Alt)
            {
                // Camera horizontal rotation
                if (Instance.Keyboard.IsKeyDown(Keys.Left))
                {
                    Camera.Rotate(-time, true);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Right))
                {
                    Camera.Rotate(time, true);
                } // Camera vertical rotation
                else if (Instance.Keyboard.IsKeyDown(Keys.PageDown))
                {
                    Camera.Rotate(-time, false);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.PageUp))
                {
                    Camera.Rotate(time, false);
                } // Camera zoom
                else if (Instance.Keyboard.IsKeyDown(Keys.Down))
                {
                    Camera.MoveToTarget(time);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Up))
                {
                    Camera.MoveToTarget(-time);
                }
            }
            else if (ModifierKeys == (Keys.Alt | Keys.Control))
            {
                // Camera horizontal rotation
                if (Instance.Keyboard.IsKeyDown(Keys.Left))
                {
                    Camera.Rotate(-time, true);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Right))
                {
                    Camera.Rotate(time, true);
                } // Camera vertical rotation
                else if (Instance.Keyboard.IsKeyDown(Keys.Down))
                {
                    Camera.Rotate(-time, false);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Up))
                {
                    Camera.Rotate(time, false);
                }
            }
            else if (ModifierKeys == Keys.Control)
            {
                // Camera pan
                float timeFactor = 3f;

                if (Instance.Keyboard.IsKeyDown(Keys.Left))
                {
                    Camera.Pan(time * timeFactor, 0f);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Right))
                {
                    Camera.Pan(-time * timeFactor, 0f);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Up))
                {
                    Camera.Pan(0f, time * timeFactor);
                }
                else if (Instance.Keyboard.IsKeyDown(Keys.Down))
                {
                    Camera.Pan(0f, -time * timeFactor);
                }
            }
        }
        #endregion Keyboard

        float LODFactor(float distance, float radius)
        {
            return radius * radius / distance;
        }

        void RenderSphere(float cx, float cy, float cz, float r, int p)
        {
            GL.PushAttrib(AttribMask.AllAttribBits);
            GL.Disable(EnableCap.Fog);
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.Dither);
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.LineStipple);
            GL.Disable(EnableCap.PolygonStipple);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.AlphaTest);
            GL.Disable(EnableCap.DepthTest);

            const float TWOPI = 6.28318530717958f;
            const float PIDIV2 = 1.57079632679489f;

            float theta1 = 0.0f;
            float theta2 = 0.0f;
            float theta3 = 0.0f;

            float ex = 0.0f;
            float ey = 0.0f;
            float ez = 0.0f;

            float px = 0.0f;
            float py = 0.0f;
            float pz = 0.0f;

            // Disallow a negative number for radius.
            if (r < 0)
                r = -r;

            // Disallow a negative number for precision.
            if (p < 0)
                p = -p;

            // If the sphere is too small, just render a OpenGL point instead.
            if (p < 4 || r <= 0)
            {
                GL.Begin(BeginMode.Points);
                GL.Vertex3(cx, cy, cz);
                GL.End();
                return;
            }

            for (int i = 0; i < p / 2; ++i)
            {
                theta1 = i * TWOPI / p - PIDIV2;
                theta2 = (i + 1) * TWOPI / p - PIDIV2;

                GL.Begin(BeginMode.TriangleStrip);
                {
                    for (int j = 0; j <= p; ++j)
                    {
                        theta3 = j * TWOPI / p;

                        ex = (float)(Math.Cos(theta2) * Math.Cos(theta3));
                        ey = (float)Math.Sin(theta2);
                        ez = (float)(Math.Cos(theta2) * Math.Sin(theta3));
                        px = cx + r * ex;
                        py = cy + r * ey;
                        pz = cz + r * ez;

                        GL.Normal3(ex, ey, ez);
                        GL.TexCoord2(-(j / (float)p), 2 * (i + 1) / (float)p);
                        GL.Vertex3(px, py, pz);

                        ex = (float)(Math.Cos(theta1) * Math.Cos(theta3));
                        ey = (float)Math.Sin(theta1);
                        ez = (float)(Math.Cos(theta1) * Math.Sin(theta3));
                        px = cx + r * ex;
                        py = cy + r * ey;
                        pz = cz + r * ez;

                        GL.Normal3(ex, ey, ez);
                        GL.TexCoord2(-(j / (float)p), 2 * i / (float)p);
                        GL.Vertex3(px, py, pz);
                    }
                }
                GL.End();
            }
            GL.PopAttrib();
        }

        void SortCullInterpolate()
        {
            SortedObjects = new List<SceneObject>();
            VisibleAvatars = new List<RenderAvatar>();

            if (RenderSettings.OcclusionCullingEnabled)
            {
                OccludedObjects = new List<SceneObject>();
            }

            lock (Prims)
            {
                foreach (RenderPrimitive obj in Prims.Values)
                {
                    obj.PositionCalculated = false;
                }

                // Calculate positions and rotations of root prims
                // Perform interpolation om objects that survive culling
                foreach (RenderPrimitive obj in Prims.Values)
                {
                    if (obj.BasePrim.ParentID != 0) continue;
                    if (!obj.Initialized) obj.Initialize();

                    obj.Step(lastFrameTime);

                    PrimPosAndRot(obj, out obj.RenderPosition, out obj.RenderRotation);
                    obj.DistanceSquared = Vector3.DistanceSquared(Camera.RenderPosition, obj.RenderPosition);
                    obj.PositionCalculated = true;

                    if (!Frustum.ObjectInFrustum(obj.RenderPosition, obj.BoundingVolume)) continue;
                    if (LODFactor(obj.DistanceSquared, obj.BoundingVolume.ScaledR) < minLODFactor) continue;

                    if (!obj.Meshed)
                    {
                        if (!obj.Meshing && meshingsRequestedThisFrame < RenderSettings.MeshesPerFrame)
                        {
                            meshingsRequestedThisFrame++;
                            MeshPrim(obj);
                        }
                    }

                    if (obj.Faces == null) continue;

                    obj.Attached = false;
                    if (obj.Occluded())
                    {
                        OccludedObjects.Add(obj);
                    }
                    else
                    {
                        SortedObjects.Add(obj);
                    }
                }

                // Calculate avatar positions and perform interpolation tasks
                lock (Avatars)
                {
                    foreach (RenderAvatar obj in Avatars.Values)
                    {
                        if (!obj.Initialized) obj.Initialize();
                        if (RenderSettings.AvatarRenderingEnabled) obj.Step(lastFrameTime);
                        PrimPosAndRot(obj, out obj.RenderPosition, out obj.RenderRotation);
                        obj.DistanceSquared = Vector3.DistanceSquared(Camera.RenderPosition, obj.RenderPosition);
                        obj.PositionCalculated = true;

                        if (!Frustum.ObjectInFrustum(obj.RenderPosition, obj.BoundingVolume)) continue;

                        VisibleAvatars.Add(obj);
                        // SortedObjects.Add(obj);
                    }
                }

                // Calculate position and rotations of child objects
                foreach (RenderPrimitive obj in Prims.Values)
                {
                    if (obj.BasePrim.ParentID == 0) continue;
                    if (!obj.Initialized) obj.Initialize();

                    obj.Step(lastFrameTime);

                    if (!obj.PositionCalculated)
                    {
                        PrimPosAndRot(obj, out obj.RenderPosition, out obj.RenderRotation);
                        obj.DistanceSquared = Vector3.DistanceSquared(Camera.RenderPosition, obj.RenderPosition);
                        obj.PositionCalculated = true;
                    }

                    if (!Frustum.ObjectInFrustum(obj.RenderPosition, obj.BoundingVolume)) continue;
                    if (LODFactor(obj.DistanceSquared, obj.BoundingVolume.ScaledR) < minLODFactor) continue;

                    if (!obj.Meshed)
                    {
                        if (!obj.Meshing && meshingsRequestedThisFrame < RenderSettings.MeshesPerFrame)
                        {
                            meshingsRequestedThisFrame++;
                            MeshPrim(obj);
                        }
                    }

                    if (obj.Faces == null) continue;

                    if (!obj.AttachedStateKnown)
                    {
                        obj.Attached = IsAttached(obj.BasePrim.ParentID);
                        obj.AttachedStateKnown = true;
                    }

                    if (obj.Occluded())
                    {
                        OccludedObjects.Add(obj);
                    }
                    else
                    {
                        SortedObjects.Add(obj);
                    }
                }
            }

            // RenderPrimitive class has IComparable implementation
            // that allows sorting by distance
            SortedObjects.Sort();
        }

        void RenderBoundingBox(SceneObject prim)
        {
            Vector3 scale = prim.BasePrim.Scale;

            GL.PushMatrix();
            GL.MultMatrix(Math3D.CreateSRTMatrix(scale, prim.RenderRotation, prim.RenderPosition));

            if (RenderSettings.UseVBO && !occludedVBOFailed)
            {
                GL.DrawElements(BeginMode.Quads, RHelp.CubeIndices.Length, DrawElementsType.UnsignedShort, IntPtr.Zero);
            }
            else
            {
                GL.VertexPointer(3, VertexPointerType.Float, 0, RHelp.CubeVertices);
                GL.DrawElements(BeginMode.Quads, RHelp.CubeIndices.Length, DrawElementsType.UnsignedShort, RHelp.CubeIndices);
            }
            GL.PopMatrix();
        }

        int boundingBoxVBO = -1;
        int boundingBoxVIndexVBO = -1;
        bool occludedVBOFailed = false;

        private void RenderOccludedObjects()
        {
            if (!RenderSettings.OcclusionCullingEnabled) return;

            GL.EnableClientState(ArrayCap.VertexArray);
            GL.ColorMask(false, false, false, false);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Lighting);

            if (RenderSettings.UseVBO && !occludedVBOFailed)
            {
                if (boundingBoxVBO == -1)
                {
                    Compat.GenBuffers(out boundingBoxVBO);
                    Compat.BindBuffer(BufferTarget.ArrayBuffer, boundingBoxVBO);
                    Compat.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(sizeof(float) * RHelp.CubeVertices.Length), RHelp.CubeVertices, BufferUsageHint.StaticDraw);
                    if (Compat.BufferSize(BufferTarget.ArrayBuffer) != sizeof(float) * RHelp.CubeVertices.Length)
                    {
                        occludedVBOFailed = true;
                        Compat.BindBuffer(BufferTarget.ArrayBuffer, 0);
                        boundingBoxVBO = -1;
                    }
                }
                else
                {
                    Compat.BindBuffer(BufferTarget.ArrayBuffer, boundingBoxVBO);
                }

                if (boundingBoxVIndexVBO == -1)
                {
                    Compat.GenBuffers(out boundingBoxVIndexVBO);
                    Compat.BindBuffer(BufferTarget.ElementArrayBuffer, boundingBoxVIndexVBO);
                    Compat.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(sizeof(ushort) * RHelp.CubeIndices.Length), RHelp.CubeIndices, BufferUsageHint.StaticDraw);
                    if (Compat.BufferSize(BufferTarget.ElementArrayBuffer) != sizeof(ushort) * RHelp.CubeIndices.Length)
                    {
                        occludedVBOFailed = true;
                        Compat.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                        boundingBoxVIndexVBO = -1;
                    }
                }
                else
                {
                    Compat.BindBuffer(BufferTarget.ElementArrayBuffer, boundingBoxVIndexVBO);
                }

                GL.VertexPointer(3, VertexPointerType.Float, 0, (IntPtr)0);
            }

            foreach (SceneObject obj in OccludedObjects)
            {
                if ((!obj.HasAlphaFaces && !obj.HasSimpleFaces)) continue;
                obj.HasSimpleFaces = true;
                obj.HasAlphaFaces = false;
                obj.StartSimpleQuery();
                RenderBoundingBox(obj);
                obj.EndSimpleQuery();
            }

            if (RenderSettings.UseVBO)
            {
                Compat.BindBuffer(BufferTarget.ArrayBuffer, 0);
                Compat.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
            }

            GL.ColorMask(true, true, true, true);
            GL.DisableClientState(ArrayCap.VertexArray);
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.Lighting);
        }

        private void RenderObjects(RenderPass pass)
        {
            if (!RenderSettings.PrimitiveRenderingEnabled) return;

            GL.EnableClientState(ArrayCap.VertexArray);
            GL.EnableClientState(ArrayCap.TextureCoordArray);
            GL.EnableClientState(ArrayCap.NormalArray);

            Vector3 myPos = Vector3.Zero;
            if (myself != null)
            {
                myPos = myself.RenderPosition;
            }
            else
            {
                myPos = Client.Self.SimPosition;
            }

            if (pass == RenderPass.Invisible)
            {
                GL.Disable(EnableCap.Texture2D);
                GL.StencilFunc(StencilFunction.Always, 1, 1);
                GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);
                GL.ColorMask(false, false, false, false);
                GL.StencilMask(0);
            }

            int nrPrims = SortedObjects.Count;
            for (int i = 0; i < nrPrims; i++)
            {
                //RenderBoundingBox(SortedPrims[i]);

                // When rendering alpha faces, draw from back towards the camers
                // otherwise from those closest to camera, to the farthest
                int ix = pass == RenderPass.Alpha ? nrPrims - i - 1 : i;
                SceneObject obj = SortedObjects[ix];

                if (obj is RenderPrimitive)
                {
                    // Don't render objects that are outside the draw distane
                    if (FindClosestDistanceSquared(myPos, obj) > drawDistanceSquared) continue;
                    if (pass == RenderPass.Simple || pass == RenderPass.Alpha)
                    {
                        obj.StartQuery(pass);
                        obj.Render(pass, ix, this, lastFrameTime);
                        obj.EndQuery(pass);
                    }
                    else
                    {
                        obj.Render(pass, ix, this, lastFrameTime);
                    }
                }
            }

            if (pass == RenderPass.Invisible)
            {
                GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
                GL.ColorMask(true, true, true, true);
                GL.StencilFunc(StencilFunction.Notequal, 1, 1);
                GL.StencilMask(uint.MaxValue);
            }

            GL.Disable(EnableCap.Texture2D);
            GL.DisableClientState(ArrayCap.VertexArray);
            GL.DisableClientState(ArrayCap.TextureCoordArray);
            GL.DisableClientState(ArrayCap.NormalArray);
        }

        int texturesRequestedThisFrame;
        int meshingsRequestedThisFrame;
        int meshingsRequestedLastFrame;
        int framesSinceReflection = 0;
        float timeSinceReflection = 0f;

        private void Render(bool picking)
        {
            // If we have more than one active GL control on the screen, make this one active
            glControl.MakeCurrent();

            SortCullInterpolate();

            if (picking)
            {
                GL.ClearColor(1f, 1f, 1f, 1f);
            }
            else
            {
                if (RenderSettings.WaterReflections)
                {
                    framesSinceReflection++;
                    timeSinceReflection += lastFrameTime;

                    if (Camera.Modified || (framesSinceReflection > 4 && timeSinceReflection > 0.1f))
                    {
                        GL.ClearColor(0, 0, 0, 0);
                        CreateReflectionTexture(Client.Network.CurrentSim.WaterHeight, 512);
                        CreateRefractionDepthTexture(Client.Network.CurrentSim.WaterHeight, 512);
                        glControl_Resize(null, null);
                        framesSinceReflection = 0;
                        timeSinceReflection = 0f;
                    }
                }
                GL.ClearColor(0.39f, 0.58f, 0.93f, 1.0f);
            }

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            GL.LoadIdentity();

            // Setup wireframe or solid fill drawing mode
            if (Wireframe && !picking)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }
            else
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            Camera.LookAt();

            GL.Light(LightName.Light0, LightParameter.Position, sunPos);

            // Push the world matrix
            GL.PushMatrix();

            if (!Camera.Manual && trackedObject != null)
            {
                // If camera is locked onto our avatar, follow us
                if (trackedObject == myself)
                {
                    Vector3 camPos = myself.RenderPosition + new Vector3(-4, 0, 1) * Client.Self.Movement.BodyRotation;
                    Camera.Position = camPos;
                    Camera.FocalPoint = myself.RenderPosition + new Vector3(5, 0, 0) * Client.Self.Movement.BodyRotation;
                }
                else
                {
                    if (lastTrackedObjectPos == RHelp.InvalidPosition)
                    {
                        lastTrackedObjectPos = trackedObject.RenderPosition;
                    }
                    else if (lastTrackedObjectPos != trackedObject.RenderPosition)
                    {
                        Vector3 diffPos = (trackedObject.RenderPosition - lastTrackedObjectPos);
                        Camera.Position += diffPos;
                        Camera.FocalPoint += diffPos;
                        lastTrackedObjectPos = trackedObject.RenderPosition;
                    }
                }
            }

            if (Camera.Modified)
            {
                GL.GetFloat(GetPName.ProjectionMatrix, out ProjectionMatrix);
                GL.GetFloat(GetPName.ModelviewMatrix, out ModelMatrix);
                GL.GetInteger(GetPName.Viewport, Viewport);
                Frustum.CalculateFrustum(ProjectionMatrix, ModelMatrix);
                UpdateCamera();
                Camera.Modified = false;
                Camera.Step(lastFrameTime);
            }

            if (picking)
            {
                GL.Disable(EnableCap.Lighting);
                terrain.Render(RenderPass.Picking, 0, this, lastFrameTime);
                RenderObjects(RenderPass.Picking);
                RenderAvatars(RenderPass.Picking);
                GLHUDBegin();
                RenderText(RenderPass.Picking);
                GLHUDEnd();
                GL.Enable(EnableCap.Lighting);
            }
            else
            {
                texturesRequestedThisFrame = 0;
                meshingsRequestedLastFrame = meshingsRequestedThisFrame;
                meshingsRequestedThisFrame = 0;

                CheckKeyboard(lastFrameTime);

                terrain.Render(RenderPass.Simple, 0, this, lastFrameTime);

                // Alpha mask elements, no blending, alpha test for A > 0.5
                GL.Enable(EnableCap.AlphaTest);
                RenderObjects(RenderPass.Simple);
                RenderAvatarsSkeleton(RenderPass.Simple);
                RenderObjects(RenderPass.Invisible);
                RenderAvatars(RenderPass.Simple);
                GL.Disable(EnableCap.AlphaTest);

                GL.DepthMask(false);
                RenderOccludedObjects();

                // Alpha blending elements, disable writing to depth buffer
                GL.Enable(EnableCap.Blend);
                RenderWater();
                RenderObjects(RenderPass.Alpha);
                GL.DepthMask(true);

                GLHUDBegin();
                RenderText(RenderPass.Simple);
                RenderStats();
                chatOverlay.RenderChat(lastFrameTime, RenderPass.Simple);
                GLHUDEnd();
                GL.Disable(EnableCap.Blend);
            }

            // Pop the world matrix
            GL.PopMatrix();
        }

        private void GluPerspective(float fovy, float aspect, float zNear, float zFar)
        {
            float fH = (float)Math.Tan(fovy / 360 * (float)Math.PI) * zNear;
            float fW = fH * aspect;
            GL.Frustum(-fW, fW, -fH, fH, zNear, zFar);
        }

        public bool TryPick(int x, int y, out object picked, out int faceID)
        {
            Vector3 worldPos;
            return TryPick(x, y, out picked, out faceID, out worldPos);
        }

        public bool TryPick(int x, int y, out object picked, out int faceID, out Vector3 worldPos)
        {
            // Save old attributes
            GL.PushAttrib(AttribMask.AllAttribBits);

            // Disable some attributes to make the objects flat / solid color when they are drawn
            GL.Disable(EnableCap.Fog);
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.Dither);
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.LineStipple);
            GL.Disable(EnableCap.PolygonStipple);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.AlphaTest);

            Render(true);

            byte[] color = new byte[4];
            GL.ReadPixels(x, glControl.Height - y, 1, 1, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, color);
            float depth = 0f;
            GL.ReadPixels(x, glControl.Height - y, 1, 1, OpenTK.Graphics.OpenGL.PixelFormat.DepthComponent, PixelType.Float, ref depth);
            OpenTK.Vector3 worldPosTK = OpenTK.Vector3.Zero;
            Math3D.GluUnProject(x, glControl.Height - y, depth, ModelMatrix, ProjectionMatrix, Viewport, out worldPosTK);
            worldPos = RHelp.OMVVector3(worldPosTK);
            GL.PopAttrib();

            int primID = Utils.BytesToUInt16(color, 0);
            faceID = color[2];

            picked = null;

            if (color[3] == 253) // Terrain
            {
                int vertexIndex = Utils.BytesToInt(new byte[] { color[0], color[1], color[2], 0 });
                ColorVertex cv;
                if (terrain.TryGetVertex(vertexIndex, out cv))
                {
                    picked = cv.Vertex.Position;
                    return true;
                }
            }
            else if (color[3] == 254) // Avatar
            {
                lock (VisibleAvatars)
                {
                    foreach (var avatar in VisibleAvatars)
                    {
                        for (int i = 0; i < avatar.data.Length; i++)
                        {
                            var face = avatar.data[i];
                            if (face != null && face.PickingID == primID)
                            {
                                picked = avatar;
                                break;
                            }
                        }
                    }
                }

                if (picked != null)
                {
                    return true;
                }
            }
            else if (color[3] == 255) // Prim
            {
                lock (SortedObjects)
                {
                    foreach (SceneObject obj in SortedObjects)
                    {
                        if (!(obj is RenderPrimitive)) continue;
                        RenderPrimitive prim = (RenderPrimitive)obj;

                        if (obj.BasePrim.LocalID == 0)
                            continue;

                        foreach (var face in prim.Faces)
                        {
                            if (face.UserData == null) continue;
                            if (((FaceData)face.UserData).PickingID == primID)
                            {
                                picked = prim;
                                break;
                            }
                        }

                        if (picked != null) break;
                    }
                }
            }

            return picked != null;
        }

        /// <summary>
        /// Select shiny shader as the current shader
        /// </summary>
        public void StartShiny()
        {
            if (RenderSettings.EnableShiny)
            {
                shinyProgram.Start();
            }
        }

        public bool TryGetTextureInfo(UUID textureID, out TextureInfo info)
        {
            info = null;

            if (TexturesPtrMap.ContainsKey(textureID))
            {
                info = TexturesPtrMap[textureID];
                return true;
            }

            return false;
        }


        public static readonly UUID invisi1 = new UUID("38b86f85-2575-52a9-a531-23108d8da837");
        public static readonly UUID invisi2 = new UUID("e97cf410-8e61-7005-ec06-629eba4cd1fb");

        public void DownloadTexture(TextureLoadItem item, bool force)
        {
            if (force || texturesRequestedThisFrame < RenderSettings.TexturesToDownloadPerFrame)
            {
                lock (TexturesPtrMap)
                {
                    if (TexturesPtrMap.ContainsKey(item.TeFace.TextureID))
                    {
                        item.Data.TextureInfo = TexturesPtrMap[item.TeFace.TextureID];
                    }
                    else if (item.TeFace.TextureID == invisi1 || item.TeFace.TextureID == invisi2)
                    {
                        TexturesPtrMap[item.TeFace.TextureID] = item.Data.TextureInfo;
                        TexturesPtrMap[item.TeFace.TextureID].HasAlpha = false;
                        TexturesPtrMap[item.TeFace.TextureID].IsInvisible = true;
                    }
                    else
                    {
                        TexturesPtrMap[item.TeFace.TextureID] = item.Data.TextureInfo;

                        if (item.TextureData == null && item.TGAData == null)
                        {
                            if (CacheDecodedTextures && RHelp.LoadCachedImage(item.TeFace.TextureID, out item.TGAData, out item.Data.TextureInfo.HasAlpha, out item.Data.TextureInfo.FullAlpha, out item.Data.TextureInfo.IsMask))
                            {
                                PendingTextures.Enqueue(item);
                            }
                            else if (Client.Assets.Cache.HasAsset(item.Data.TextureInfo.TextureID))
                            {
                                item.LoadAssetFromCache = true;
                                PendingTextures.Enqueue(item);
                            }
                            else if (!item.Data.TextureInfo.FetchFailed)
                            {
                                TextureDownloadCallback handler = (state, asset) =>
                                    {
                                        switch (state)
                                        {
                                            case TextureRequestState.Finished:
                                                item.TextureData = asset.AssetData;
                                                PendingTextures.Enqueue(item);
                                                break;

                                            case TextureRequestState.Aborted:
                                            case TextureRequestState.NotFound:
                                            case TextureRequestState.Timeout:
                                                item.Data.TextureInfo.FetchFailed = true;
                                                break;
                                        }
                                    };

                                if (item.ImageType == ImageType.ServerBaked && !string.IsNullOrEmpty(item.BakeName))
                                { // Server side bake
                                    Client.Assets.RequestServerBakedImage(item.AvatarID, item.TeFace.TextureID, item.BakeName, handler);
                                }
                                else
                                { // Regular texture 
                                    Client.Assets.RequestImage(item.TeFace.TextureID, item.ImageType, handler);
                                }

                                texturesRequestedThisFrame++;
                            }
                        }
                        else
                        {
                            PendingTextures.Enqueue(item);
                        }
                    }
                }
            }
        }

        private void CalculateBoundingBox(RenderPrimitive rprim)
        {
            Primitive prim = rprim.BasePrim;

            // Calculate bounding volumes for each prim and adjust textures
            rprim.BoundingVolume = new BoundingVolume();
            for (int j = 0; j < rprim.Faces.Count; j++)
            {
                Primitive.TextureEntryFace teFace = prim.Textures.GetFace((uint)j);
                if (teFace == null) continue;

                Face face = rprim.Faces[j];
                FaceData data = new FaceData();

                data.BoundingVolume.CreateBoundingVolume(face, prim.Scale);
                rprim.BoundingVolume.AddVolume(data.BoundingVolume, prim.Scale);

                // With linear texture animation in effect, texture repeats and offset are ignored
                if ((prim.TextureAnim.Flags & Primitive.TextureAnimMode.ANIM_ON) != 0
                    && (prim.TextureAnim.Flags & Primitive.TextureAnimMode.ROTATE) == 0
                    && (prim.TextureAnim.Face == 255 || prim.TextureAnim.Face == j))
                {
                    teFace.RepeatU = 1;
                    teFace.RepeatV = 1;
                    teFace.OffsetU = 0;
                    teFace.OffsetV = 0;
                }

                // Sculpt UV vertically flipped compared to prims. Flip back
                if (prim.Sculpt != null && prim.Sculpt.SculptTexture != UUID.Zero && prim.Sculpt.Type != SculptType.Mesh)
                {
                    teFace = (Primitive.TextureEntryFace)teFace.Clone();
                    teFace.RepeatV *= -1;
                }

                // Texture transform for this face
                renderer.TransformTexCoords(face.Vertices, face.Center, teFace, prim.Scale);

                // Set the UserData for this face to our FaceData struct
                face.UserData = data;
                rprim.Faces[j] = face;
            }
        }

        private void MeshPrim(RenderPrimitive rprim)
        {
            if (rprim.Meshing) return;

            rprim.Meshing = true;
            Primitive prim = rprim.BasePrim;

            // Regular prim
            if (prim.Sculpt == null || prim.Sculpt.SculptTexture == UUID.Zero)
            {
                DetailLevel detailLevel = RenderSettings.PrimRenderDetail;
                if (RenderSettings.AllowQuickAndDirtyMeshing)
                {
                    if (prim.Flexible == null && prim.Type == PrimType.Box &&
                        prim.PrimData.ProfileHollow == 0 &&
                        prim.PrimData.PathTwist == 0 &&
                        prim.PrimData.PathTaperX == 0 &&
                        prim.PrimData.PathTaperY == 0 &&
                        prim.PrimData.PathSkew == 0 &&
                        prim.PrimData.PathShearX == 0 &&
                        prim.PrimData.PathShearY == 0 &&
                        prim.PrimData.PathRevolutions == 1 &&
                        prim.PrimData.PathRadiusOffset == 0)
                        detailLevel = DetailLevel.Low;//Its a box or something else that can use lower meshing
                }
                FacetedMesh mesh = renderer.GenerateFacetedMesh(prim, detailLevel);
                rprim.Faces = mesh.Faces;
                CalculateBoundingBox(rprim);
                rprim.Meshing = false;
                rprim.Meshed = true;
            }
            else
            {
                PendingTasks.Enqueue(GenerateSculptOrMeshPrim(rprim, prim));
                return;
            }
        }

        private GenericTask GenerateSculptOrMeshPrim(RenderPrimitive rprim, Primitive prim)
        {
            return new GenericTask(() =>
            {
                FacetedMesh mesh = null;

                try
                {
                    if (prim.Sculpt.Type != SculptType.Mesh)
                    { // Regular sculptie
                        Image img = null;

                        lock (sculptCache)
                        {
                            if (sculptCache.ContainsKey(prim.Sculpt.SculptTexture))
                            {
                                img = sculptCache[prim.Sculpt.SculptTexture];
                            }
                        }

                        if (img == null)
                        {
                            if (LoadTexture(prim.Sculpt.SculptTexture, ref img, true))
                            {
                                sculptCache[prim.Sculpt.SculptTexture] = (Bitmap)img;
                            }
                            else
                            {
                                return;
                            }
                        }

                        mesh = renderer.GenerateFacetedSculptMesh(prim, (Bitmap)img, RenderSettings.SculptRenderDetail);
                    }
                    else
                    { // Mesh
                        AutoResetEvent gotMesh = new AutoResetEvent(false);

                        Client.Assets.RequestMesh(prim.Sculpt.SculptTexture, (success, meshAsset) =>
                        {
                            if (!success || !FacetedMesh.TryDecodeFromAsset(prim, meshAsset, RenderSettings.MeshRenderDetail, out mesh))
                            {
                                Logger.Log("Failed to fetch or decode the mesh asset", Helpers.LogLevel.Warning, Client);
                            }
                            gotMesh.Set();
                        });

                        gotMesh.WaitOne(20 * 1000, false);
                    }
                }
                catch
                { }

                if (mesh != null)
                {
                    rprim.Faces = mesh.Faces;
                    CalculateBoundingBox(rprim);
                    rprim.Meshing = false;
                    rprim.Meshed = true;
                }
                else
                {
                    lock (Prims)
                    {
                        Prims.Remove(rprim.BasePrim.LocalID);
                    }
                }
            });
        }

        private void UpdatePrimBlocking(Primitive prim)
        {
            if (!RenderingEnabled) return;

            if (RenderSettings.AvatarRenderingEnabled && prim.PrimData.PCode == PCode.Avatar)
            {
                AddAvatarToScene(Client.Network.CurrentSim.ObjectsAvatars[prim.LocalID]);
                return;
            }

            // Skip foliage
            if (prim.PrimData.PCode != PCode.Prim) return;
            if (!RenderSettings.PrimitiveRenderingEnabled) return;

            if (prim.Textures == null) return;

            RenderPrimitive rPrim = null;
            if (Prims.TryGetValue(prim.LocalID, out rPrim))
            {
                rPrim.AttachedStateKnown = false;
            }
            else
            {
                rPrim = new RenderPrimitive();
                rPrim.Meshed = false;
                rPrim.BoundingVolume = new BoundingVolume();
                rPrim.BoundingVolume.FromScale(prim.Scale);
            }

            rPrim.BasePrim = prim;
            lock (Prims) Prims[prim.LocalID] = rPrim;
        }

        private bool LoadTexture(UUID textureID, ref Image texture, bool removeAlpha)
        {
            ManualResetEvent gotImage = new ManualResetEvent(false);
            Image img = null;

            try
            {
                gotImage.Reset();
                bool hasAlpha, fullAlpha, isMask;
                byte[] tgaData;
                if (RHelp.LoadCachedImage(textureID, out tgaData, out hasAlpha, out fullAlpha, out isMask))
                {
                    img = LoadTGAClass.LoadTGA(new MemoryStream(tgaData));
                }
                else
                {
                    instance.Client.Assets.RequestImage(textureID, (TextureRequestState state, AssetTexture assetTexture) =>
                        {
                            ManagedImage mi;
                            if (state == TextureRequestState.Finished && OpenJPEG.DecodeToImage(assetTexture.AssetData, out mi))
                            {

                                if (removeAlpha)
                                {
                                    if ((mi.Channels & ManagedImage.ImageChannels.Alpha) != 0)
                                    {
                                        mi.ConvertChannels(mi.Channels & ~ManagedImage.ImageChannels.Alpha);
                                    }
                                }
                                tgaData = mi.ExportTGA();
                                img = LoadTGAClass.LoadTGA(new MemoryStream(tgaData));
                                RHelp.SaveCachedImage(tgaData, textureID, (mi.Channels & ManagedImage.ImageChannels.Alpha) != 0, false, false);
                            }
                            gotImage.Set();
                        }
                    );
                    gotImage.WaitOne(30 * 1000, false);
                }
                if (img != null)
                {
                    texture = img;
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Logger.Log(e.Message, Helpers.LogLevel.Error, instance.Client, e);
                return false;
            }
        }
        #endregion Private methods (the meat)

        #region Form controls handlers
        private void btnReset_Click(object sender, EventArgs e)
        {
            InitCamera();
        }

        #endregion Form controls handlers

        #region Context menu
        /// <summary>
        /// Dynamically construct the context menu when we right click on the screen
        /// </summary>
        /// <param name="csender"></param>
        /// <param name="ce"></param>
        private void ctxObjects_Opening(object csender, System.ComponentModel.CancelEventArgs ce)
        {
            // Clear all context menu items
            ctxMenu.Items.Clear();
            ce.Cancel = false;
            ToolStripMenuItem item;

            // Always add standup button if we are sitting
            if (Instance.State.IsSitting)
            {
                item = new ToolStripMenuItem("Stand Up", null, (sender, e) =>
                {
                    instance.State.SetSitting(false, UUID.Zero);
                });
                ctxMenu.Items.Add(item);
            }

            // Was it prim that was right clicked
            if (RightclickedObject != null && RightclickedObject is RenderPrimitive)
            {
                RenderPrimitive prim = (RenderPrimitive)RightclickedObject;

                // Sit button handling
                if (!instance.State.IsSitting)
                {
                    item = new ToolStripMenuItem("Sit", null, (sender, e) =>
                    {
                        instance.State.SetSitting(true, prim.Prim.ID);
                    });

                    if (prim.Prim.Properties != null
                        && !string.IsNullOrEmpty(prim.Prim.Properties.SitName))
                    {
                        item.Text = prim.Prim.Properties.SitName;
                    }
                    ctxMenu.Items.Add(item);
                }

                // Is the prim touchable
                if ((prim.Prim.Flags & PrimFlags.Touch) != 0)
                {
                    item = new ToolStripMenuItem("Touch", null, (sender, e) =>
                    {
                        Client.Self.Grab(prim.Prim.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, RightclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                        Thread.Sleep(100);
                        Client.Self.DeGrab(prim.Prim.LocalID, Vector3.Zero, Vector3.Zero, RightclickedFaceID, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                    });

                    if (prim.Prim.Properties != null
                        && !string.IsNullOrEmpty(prim.Prim.Properties.TouchName))
                    {
                        item.Text = prim.Prim.Properties.TouchName;
                    }
                    ctxMenu.Items.Add(item);
                }

                // Can I delete and take this object?
                if ((prim.Prim.Flags & (PrimFlags.ObjectYouOwner | PrimFlags.ObjectYouOfficer)) != 0)
                {
                    // Take button
                    item = new ToolStripMenuItem("Take", null, (sender, e) =>
                    {
                        instance.MediaManager.PlayUISound(UISounds.ObjectDelete);
                        Client.Inventory.RequestDeRezToInventory(prim.Prim.LocalID);
                    });
                    ctxMenu.Items.Add(item);

                    // Delete button
                    item = new ToolStripMenuItem("Delete", null, (sender, e) =>
                    {
                        instance.MediaManager.PlayUISound(UISounds.ObjectDelete);
                        Client.Inventory.RequestDeRezToInventory(prim.Prim.LocalID, DeRezDestination.AgentInventoryTake, Client.Inventory.FindFolderForType(AssetType.TrashFolder), UUID.Random());
                    });
                    ctxMenu.Items.Add(item);


                }

                // add prim context menu items
                instance.ContextActionManager.AddContributions(ctxMenu, typeof(Primitive), prim.Prim);

            } // We right clicked on an avatar, add some context menu items
            else if (RightclickedObject != null && RightclickedObject is RenderAvatar)
            {
                RenderAvatar av = (RenderAvatar)RightclickedObject;

                // Profile button
                item = new ToolStripMenuItem("Profile", null, (sender, e) =>
                {
                    Instance.MainForm.ShowAgentProfile("", av.avatar.ID);
                });
                ctxMenu.Items.Add(item);

                if (av.avatar.ID != Client.Self.AgentID)
                {
                    // IM button
                    item = new ToolStripMenuItem("Instant Message", null, (sender, e) =>
                    {
                        Instance.TabConsole.ShowIMTab(av.avatar.ID, Instance.Names.Get(av.avatar.ID), true);
                    });
                    ctxMenu.Items.Add(item);

                    // Pay button
                    item = new ToolStripMenuItem("Pay", null, (sender, e) =>
                    {
                        (new frmPay(Instance, av.avatar.ID, Instance.Names.Get(av.avatar.ID), false)).ShowDialog();
                    });
                    ctxMenu.Items.Add(item);
                }

                // add avatar context menu items
                instance.ContextActionManager.AddContributions(ctxMenu, typeof(Avatar), av.avatar.ID);
            }

            // If we are not the sole menu item, add separator
            if (ctxMenu.Items.Count > 0)
            {
                ctxMenu.Items.Add(new ToolStripSeparator());
            }


            // Dock/undock menu item
            bool docked = !instance.TabConsole.Tabs["scene_window"].Detached;
            if (docked)
            {
                item = new ToolStripMenuItem("Undock", null, (sender, e) =>
                {
                    instance.TabConsole.SelectDefaultTab();
                    instance.TabConsole.Tabs["scene_window"].Detach(instance);
                });
            }
            else
            {
                item = new ToolStripMenuItem("Dock", null, (sender, e) =>
                {
                    Control p = Parent;
                    instance.TabConsole.Tabs["scene_window"].AttachTo(instance.TabConsole.tstTabs, instance.TabConsole.toolStripContainer1.ContentPanel);
                    if (p is Form)
                    {
                        ((Form)p).Close();
                    }
                });
            }
            ctxMenu.Items.Add(item);

            item = new ToolStripMenuItem("Options", null, (sender, e) =>
            {
                new Floater(Instance, new GraphicsPreferences(Instance), this).Show(FindForm());
            });
            ctxMenu.Items.Add(item);

            // Show hide debug panel
            if (pnlDebug.Visible)
            {
                item = new ToolStripMenuItem("Hide debug panel", null, (sender, e) =>
                {
                    pnlDebug.Visible = false;
                    Instance.GlobalSettings["scene_viewer_debug_panel"] = false;
                });
            }
            else
            {
                item = new ToolStripMenuItem("Show debug panel", null, (sender, e) =>
                {
                    pnlDebug.Visible = true;
                    Instance.GlobalSettings["scene_viewer_debug_panel"] = true;
                });
            }
            ctxMenu.Items.Add(item);
            instance.ContextActionManager.AddContributions(ctxMenu, typeof(Vector3), RightclickedPosition);
        }
        #endregion Context menu

        #region Winform hooks

        private void hsAmbient_Scroll(object sender, ScrollEventArgs e)
        {
            ambient = (float)hsAmbient.Value / 100f;
            SetSun();
        }

        private void hsDiffuse_Scroll(object sender, ScrollEventArgs e)
        {
            difuse = (float)hsDiffuse.Value / 100f;
            SetSun();
        }

        private void hsSpecular_Scroll(object sender, ScrollEventArgs e)
        {
            specular = (float)hsSpecular.Value / 100f;
            SetSun();
        }

        private void hsLOD_Scroll(object sender, ScrollEventArgs e)
        {
            minLODFactor = (float)hsLOD.Value / 5000f;
        }

        private void button_vparam_Click(object sender, EventArgs e)
        {
            //int paramid = int.Parse(textBox_vparamid.Text);
            //float weight = (float)hScrollBar_weight.Value/100f;
            float weightx = float.Parse(textBox_x.Text);
            float weighty = float.Parse(textBox_y.Text);
            float weightz = float.Parse(textBox_z.Text);

            foreach (RenderAvatar av in Avatars.Values)
            {
                //av.glavatar.applyMorph(av.avatar,paramid,weight);
                av.glavatar.skel.deformbone(comboBox1.Text, new Vector3(float.Parse(textBox_sx.Text), float.Parse(textBox_sy.Text), float.Parse(textBox_sz.Text)), Quaternion.CreateFromEulers((float)(Math.PI * (weightx / 180)), (float)(Math.PI * (weighty / 180)), (float)(Math.PI * (weightz / 180))));

                foreach (GLMesh mesh in av.glavatar._meshes.Values)
                {
                    mesh.applyjointweights();
                }

            }
        }

        private void textBox_vparamid_TextChanged(object sender, EventArgs e)
        {

        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

            string bone = comboBox1.Text;
            foreach (RenderAvatar av in Avatars.Values)
            {
                Bone b;
                if (av.glavatar.skel.mBones.TryGetValue(bone, out b))
                {
                    textBox_sx.Text = (b.scale.X - 1.0f).ToString();
                    textBox_sy.Text = (b.scale.Y - 1.0f).ToString();
                    textBox_sz.Text = (b.scale.Z - 1.0f).ToString();

                    float x, y, z;
                    b.rot.GetEulerAngles(out x, out y, out z);
                    textBox_x.Text = x.ToString();
                    textBox_y.Text = y.ToString();
                    textBox_z.Text = z.ToString();

                }

            }


        }

        private void textBox_y_TextChanged(object sender, EventArgs e)
        {

        }

        private void textBox_z_TextChanged(object sender, EventArgs e)
        {

        }

        private void comboBox_morph_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void button1_Click(object sender, EventArgs e)
        {
            foreach (RenderAvatar av in Avatars.Values)
            {
                int id = -1;

                //foreach (VisualParamEx vpe in VisualParamEx.morphParams.Values)
                //{
                //    if (vpe.Name == comboBox_morph.Text)
                //    {
                //        id = vpe.ParamID;
                //        break;
                //    }
                //
                //}

                av.glavatar.applyMorph(av.avatar, id, float.Parse(textBox_morphamount.Text));

                foreach (GLMesh mesh in av.glavatar._meshes.Values)
                {
                    mesh.applyjointweights();
                }

            }



        }

        private void gbZoom_Enter(object sender, EventArgs e)
        {

        }

        private void button_driver_Click(object sender, EventArgs e)
        {
            /*
            foreach (RenderAvatar av in Avatars.Values)
            {
                int id = -1;
                foreach (VisualParamEx vpe in VisualParamEx.drivenParams.Values)
                {
                    if (vpe.Name == comboBox_driver.Text)
                    {
                        id = vpe.ParamID;
                        break;
                    }

                }
                av.glavatar.applyMorph(av.avatar, id, float.Parse(textBox_driveramount.Text));

                foreach (GLMesh mesh in av.glavatar._meshes.Values)
                {
                    mesh.applyjointweights();
                }

            }
            */
        }

        bool miscEnabled = true;
        private void cbMisc_CheckedChanged(object sender, EventArgs e)
        {
            miscEnabled = cbMisc.Checked;
            RenderSettings.OcclusionCullingEnabled = miscEnabled;
        }

        #endregion

        private void txtChat_TextChanged(object sender, EventArgs e)
        {
            if (txtChat.Text.Length > 0)
            {
                btnSay.Enabled = cbChatType.Enabled = true;
                if (!txtChat.Text.StartsWith("/"))
                {
                    if (!Instance.State.IsTyping && !Instance.GlobalSettings["no_typing_anim"])
                    {
                        Instance.State.SetTyping(true);
                    }
                }
            }
            else
            {
                btnSay.Enabled = cbChatType.Enabled = false;
                if (!Instance.GlobalSettings["no_typing_anim"])
                {
                    Instance.State.SetTyping(false);
                }
            }
        }

        private void txtChat_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            ChatConsole chat = (ChatConsole)Instance.TabConsole.Tabs["chat"].Control;

            if (e.Shift)
                chat.ProcessChatInput(txtChat.Text, ChatType.Whisper);
            else if (e.Control)
                chat.ProcessChatInput(txtChat.Text, ChatType.Shout);
            else
                chat.ProcessChatInput(txtChat.Text, ChatType.Normal);

            txtChat.Text = string.Empty;
        }

        private void btnSay_Click(object sender, EventArgs e)
        {
            ChatConsole chat = (ChatConsole)Instance.TabConsole.Tabs["chat"].Control;
            chat.ProcessChatInput(txtChat.Text, (ChatType)cbChatType.SelectedIndex);
            txtChat.Select();
            txtChat.Text = string.Empty;
        }

    }
}
