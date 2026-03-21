using PEPCO;
using Digi;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Resources;
using System.Runtime.Remoting.Metadata.W3cXsd2001;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;
using static PEPCO.ScriptHelpers;

namespace PEPCO
{
    [MyTextSurfaceScript("TrawlingNetExtTSS", "Trawling Net Monitor")]
    public class TrawlingNet_ExtTSS : MyTSSCommon
    {
        #region Fields & Constants

        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        private readonly IMyTerminalBlock TerminalBlock;
        private IMyFunctionalBlock TrawlingNetBlock;
        private FishCollectorComponent logic;


        // Viewport and Raycast
        private RectangleF _viewport;
        
        private TrawlingNetContent TrawlingNetContent => logic?.Content;

        #endregion

        #region Lifecycle

        public TrawlingNet_ExtTSS(IMyTextSurface surface, IMyCubeBlock block, Vector2 size) : base(surface, block, size)
        {
            TerminalBlock = (IMyTerminalBlock)block;
            TerminalBlock.OnMarkForClose += BlockMarkedForClose;
        }

        public override void Dispose()
        {
            base.Dispose();
            TerminalBlock.OnMarkForClose -= BlockMarkedForClose;
        }

        void BlockMarkedForClose(IMyEntity ent)
        {
            Dispose();
        }

        #endregion

        #region Main Loop

        public override void Run()
        {
            try
            {
                base.Run();

                // Cache the TrawlingNetBlock and its logic component for efficiency, but validate their existence each tick in case of changes in the grid
                if (TrawlingNetBlock == null)
                {
                    // Get the Grid
                    var grid = TerminalBlock.CubeGrid;
                    if (grid == null) { DrawMessage("Error_Device - Grid dirty"); return; }

                    // Get the first block of the type and subtype MyObjectBuilder_FunctionalBlock and AQD_LG_TrawlingNet
                    var trawlingblock = grid.GetFatBlocks<IMyFunctionalBlock>().FirstOrDefault(b => b.BlockDefinition.SubtypeId == "AQD_LG_TrawlingNet");
                    if (trawlingblock == null) { DrawMessage("Error_Device - Trawling net dirty"); return; }

                    logic = trawlingblock?.GameLogic?.GetAs<FishCollectorComponent>();

                    // 1. Validation Checks
                    if (logic == null) { DrawMessage("Error_Device - Trawling net logic dirty"); return; }
                }
                // 3. Render
                Draw();
            }
            catch (Exception e)
            {
                DrawError(e);
            }
        }

        #endregion

        #region Drawing Logic

        void Draw()
        {
            Vector2 viewportPos = (Surface.TextureSize - Surface.SurfaceSize) / 2f;
            _viewport = new RectangleF(viewportPos, Surface.SurfaceSize);

            if (logic?.Block == null) { DrawMessage("Error_Device"); return; }
            logic.Block.RefreshCustomInfo();

            // Print the same content as the custom data of the trawling net block
            using (var frame = Surface.DrawFrame())
            {
                string contentText = logic.Block.CustomInfo;
                var textSprite = MySprite.CreateText(contentText, "White", Color.Cyan, 0.7f, TextAlignment.LEFT);
                textSprite.Position = ((Surface.TextureSize - Surface.SurfaceSize) * 0.5f) + new Vector2(16, 16);
                frame.Add(textSprite);
            }

        }
        #endregion

        #region Reusable Helpers

        private MySprite CreateSprite(string data, Vector2 position, Vector2 size, Color color, float rotation = 0f, TextAlignment alignment = TextAlignment.CENTER)
        {
            return new MySprite()
            {
                Type = SpriteType.TEXTURE,
                Data = data,
                Position = position,
                Size = size,
                Color = color,
                RotationOrScale = rotation,
                Alignment = alignment
            };
        }

        private MySprite CopySprite(MySprite original, Vector2 posOffset, Vector2? sizeOverride = null)
        {
            var s = original;
            s.Position += posOffset;
            if (sizeOverride.HasValue) s.Size = sizeOverride.Value;
            return s;
        }

        // Added 'aspectCorrection' parameter
        private Vector2 CalculateScreenPosition(Vector2 viewportCenter, Vector2 halfScreenSize, double latFraction, double longFraction, float zoom, float hFlip, double latOffset, double longOffset, float aspectCorrection)
        {
            // Calculate the movement delta relative to the map scale
            double xDelta = (longFraction + longOffset) * zoom * hFlip * aspectCorrection;
            double yDelta = (latFraction + latOffset) * zoom;

            // Apply delta to the SCREEN size (not the texture center)
            float xPos = viewportCenter.X + (float)(xDelta * halfScreenSize.X);
            float yPos = viewportCenter.Y + (float)(yDelta * halfScreenSize.Y);

            return new Vector2(xPos, yPos);
        }

        private bool IsPointInSprite(Vector2 point, MySprite sprite)
        {
            if (!sprite.Position.HasValue || !sprite.Size.HasValue) return false;

            Vector2 pos = sprite.Position.Value;
            Vector2 size = sprite.Size.Value;

            float halfW = size.X / 2;
            float halfH = size.Y / 2;

            return point.X >= pos.X - halfW && point.X <= pos.X + halfW &&
                   point.Y >= pos.Y - halfH && point.Y <= pos.Y + halfH;
        }


        void DrawOther(string dataInput)
        {
            _viewport = new RectangleF((Surface.TextureSize - Surface.SurfaceSize) / 2f, Surface.SurfaceSize);
            using (var frame = Surface.DrawFrame())
            {
                frame.Add(CreateSprite(dataInput, _viewport.Center, _viewport.Size, Color.White));
            }
        }

        void DrawError(Exception e)
        {
            MyLog.Default.WriteLineAndConsole($"{e.Message}\n{e.StackTrace}");
            try
            {
                using (var frame = Surface.DrawFrame())
                {
                    frame.Add(CreateSprite("SquareSimple", Surface.TextureSize / 2, Surface.TextureSize, Color.Black));

                    var text = MySprite.CreateText($"ERROR: {e.Message}\n{e.StackTrace}\n\nPlease send screenshot to mod author.\n{MyAPIGateway.Utilities.GamePaths.ModScopeName}", "White", Color.Red, 0.7f, TextAlignment.LEFT);
                    text.Position = ((Surface.TextureSize - Surface.SurfaceSize) * 0.5f) + new Vector2(16, 16);
                    frame.Add(text);
                }
            }
            catch (Exception e2)
            {
                MyAPIGateway.Utilities.ShowNotification($"[ ERROR: {GetType().FullName}: {e.Message} && {e2.Message} ]", 10000, MyFontEnum.Red);
            }
        }

        public void DrawMessage(string message)
        {
            MyLog.Default.WriteLineAndConsole(message);
            try
            {
                using (var frame = Surface.DrawFrame())
                {
                    frame.Add(CreateSprite("SquareSimple", Surface.TextureSize / 2, Surface.TextureSize, Color.Black));
                    var text = MySprite.CreateText(message, "White", Color.Yellow, 0.7f, TextAlignment.LEFT);
                    text.Position = ((Surface.TextureSize - Surface.SurfaceSize) * 0.5f) + new Vector2(16, 16);
                    frame.Add(text);
                }
            }
            catch
            {
                MyAPIGateway.Utilities.ShowNotification(message, 5000, MyFontEnum.Red);
            }
        }

        #endregion
    }
}