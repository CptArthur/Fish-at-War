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

                // Cache the TrawlingNetBlock and its logic component for efficiency
                if (TrawlingNetBlock == null)
                {
                    // Get the Grid
                    var grid = TerminalBlock.CubeGrid;
                    if (grid == null) { DrawMessage("Error_Device - Grid dirty"); return; }

                    // Get the first block of the type and subtype
                    var trawlingblock = grid.GetFatBlocks<IMyFunctionalBlock>().FirstOrDefault(b => b.BlockDefinition.SubtypeId == "AQD_LG_TrawlingNet");
                    if (trawlingblock == null) { DrawMessage("Error_Device - No trawling net found."); return; }

                    logic = trawlingblock?.GameLogic?.GetAs<FishCollectorComponent>();

                    // 1. Validation Checks
                    if (logic == null) { DrawMessage("Error_Device - Trawling net logic dirty. Logic missing.\nTell UZAR!"); return; }

                    // --- THE FIX: Cache the block so we don't scan the grid again! ---
                    TrawlingNetBlock = trawlingblock;
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
            logic.Block.RefreshCustomInfo();

            // 1. Calculate safe drawing area
            // SurfaceSize is the visible part; TextureSize includes the hidden bezels.
            Vector2 surfaceSize = Surface.SurfaceSize;
            Vector2 textureSize = Surface.TextureSize;
            Vector2 viewportOffset = (textureSize - surfaceSize) / 2f;

            // 2. Calculate scale factor (Targeting 512px as the '1.0' baseline)
            float minAxis = Math.Min(surfaceSize.X, surfaceSize.Y);
            float uiScale = minAxis / 512f;

            // Adjust font size. 0.7f is your base; uiScale adjusts it for the LCD res.
            float scaledFontSize = 1f * uiScale;
            float scaledPadding = 16f * uiScale;

            using (var frame = Surface.DrawFrame())
            {
                // --- FIX 1: DRAW BACKGROUND ---
                // This ensures the whole screen is your dark blue color
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXTURE,
                    Data = "SquareSimple",
                    Position = textureSize / 2f,
                    Size = textureSize,
                    Color = new Color(12, 25, 33, 255),
                    Alignment = TextAlignment.CENTER
                });

                // 3. Prepare Content
                string contentText = logic.Block.CustomInfo ?? "";
                string warningText = FishCollectorComponent.WARNINGTEXT ?? "";

                if (!string.IsNullOrEmpty(warningText) && contentText.Contains(warningText))
                {
                    contentText = contentText.Replace(warningText, "").TrimEnd();
                }

                // --- FIX 2: TEXT POSITIONING ---
                // We add viewportOffset to move past the LCD bezels into the visible area
                Vector2 textPos = viewportOffset + new Vector2(scaledPadding, scaledPadding);

                var textSprite = MySprite.CreateText(contentText, "White", Color.Cyan, scaledFontSize, TextAlignment.LEFT);
                textSprite.Position = textPos;
                frame.Add(textSprite);

                // 4. Warning Logic
                if (logic.ContentToBeLost)
                {
                    // Calculate dynamic Y offset based on line count
                    // 'White' font height is roughly 30px at 1.0 scale
                    float lineSpacing = 30f * scaledFontSize;
                    int lineCount = contentText.Split('\n').Length;
                    float yOffset = (lineCount * lineSpacing) + (8f * uiScale); // Extra gap

                    var redSprite = MySprite.CreateText("Not enough inventory space!\nNet content will be lost.", "White", Color.Red, scaledFontSize, TextAlignment.LEFT);
                    redSprite.Position = textPos + new Vector2(0, yOffset);
                    frame.Add(redSprite);
                }
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