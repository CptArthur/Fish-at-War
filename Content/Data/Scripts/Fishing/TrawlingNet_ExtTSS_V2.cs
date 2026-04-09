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
using VRage;


namespace PEPCO
{
    [MyTextSurfaceScript("TrawlingNetExtTSSV2", "Trawling Net Monitor V2")]
    public class TrawlingNet_ExtTSS_V2 : MyTSSCommon
    {
        #region Fields & Constants

        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        private readonly IMyTerminalBlock TerminalBlock;
        private IMyFunctionalBlock TrawlingNetBlock;
        private FishCollectorComponent logic;

        private const float ROTATION_RATE = 0.52f; // 1 full turn every 2 seconds
        private float _spinnerRotation = 0f; // 

        // Viewport and Raycast
        private RectangleF _viewport;

        private TrawlingNetContent TrawlingNetContent => logic?.Content;
        private FishCollectorComponent.NetStatusEvaluator _evaluator => logic?.Evaluator;

        // Virtual Canvas Scaling
        private float _uiScale;
        private Vector2 _canvasTopLeft;

        #endregion

        #region Lifecycle

        public TrawlingNet_ExtTSS_V2(IMyTextSurface surface, IMyCubeBlock block, Vector2 size) : base(surface, block, size)
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
                    if (logic == null || _evaluator == null) { DrawMessage("Error_Device - Trawling net logic dirty. Logic or Evaluator missing.\nTell UZAR!"); return; }

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
            _spinnerRotation = (_spinnerRotation + ROTATION_RATE) % MathHelper.TwoPi;

            // --- NEW: Calculate Virtual Canvas Scale & Position ---
            // 1. Find the smallest axis so we maintain a perfect 1:1 aspect ratio
            float minAxis = Math.Min(Surface.SurfaceSize.X, Surface.SurfaceSize.Y);

            // 2. Calculate how much we need to scale our 512x512 design
            _uiScale = minAxis / 512f;

            // 3. Find the true center of the rendering texture and offset by half our scaled virtual canvas.
            // This perfectly centers your UI on Wide LCDs!
            _canvasTopLeft = (Surface.TextureSize / 2f) - new Vector2(256f * _uiScale);
            // ------------------------------------------------------

            float netContentPercentage = logic.NetContentPercentage;
            float efficiencyRatio = logic.Evaluator.Efficiency;
            float capacityRatio = netContentPercentage / 100f;

            // Maximum dimensions based on your design
            Vector2 barSize = new Vector2(26.8534f, 58.0365f);

            using (var frame = Surface.DrawFrame())
            {
                //// --- 1. THE BACKGROUND ---
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXTURE,
                    Data = "SquareSimple",
                    Position = Surface.TextureSize / 2f, // True center of the screen
                    Size = Surface.TextureSize,          // Stretches across the whole screen

                    Color = new Color(12, 25, 33, 255),

                    Alignment = TextAlignment.CENTER
                });

                // TrawlingUIDefault
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXTURE,
                    Data = "TrawlingUIDefault",
                    Position = ScalePos(256.0000f, 256.0000f),
                    Size = ScaleSize(512.0000f, 512.0000f),
                    Color = new Color(255, 255, 255, 255),
                    Alignment = TextAlignment.CENTER,
                    RotationOrScale = 0.0000f,  // Radians (Do not scale!)
                });

                // TrawlingSpinner
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXTURE,
                    Data = "TrawlingSpinner",
                    Position = ScalePos(33.6316f, 93.7631f),
                    Size = ScaleSize(24.0000f, 24.0000f),
                    Color = logic.Evaluator.GetStatusColor(),
                    Alignment = TextAlignment.CENTER,
                    RotationOrScale = _spinnerRotation,  // Radians (Do not scale!)
                });

                // Status Message (e.g. "Net is full!", "No valid location!", etc.)
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = logic.Evaluator.GetStatusMessage(),
                    Position = ScalePos(155.2000f, 78.6000f),
                    Color = logic.Evaluator.GetStatusColor(),
                    FontId = "White",
                    Alignment = TextAlignment.CENTER,
                    RotationOrScale = ScaleText(logic.Evaluator.GetStatusScale()),  // Scale applied to Text!
                });

                // Only show stuff below if operating
                if (!_evaluator.IsOperating) return;

                // [4] TEXT "Efficiency: "
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = $"Efficiency: {(_evaluator.Efficiency * 100):F0}%",
                    Position = ScalePos(62.4000f, 314.0000f),
                    Color = new Color(255, 255, 255, 255),
                    FontId = "White",
                    Alignment = TextAlignment.LEFT,
                    RotationOrScale = ScaleText(0.80f),
                });

                // [7] TEXT "Speed: "
                Color speedColor = _evaluator.Efficiency < 1f ? Color.OrangeRed : Color.White;
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = $"Speed: {_evaluator.SpeedHint}",
                    Position = ScalePos(63.6000f, 352.8000f),
                    Color = new Color(255, 255, 255, 255),
                    FontId = "White",
                    Alignment = TextAlignment.LEFT,
                    RotationOrScale = ScaleText(0.80f),
                });

                // [5] TEXT "Capacity:"
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = $"Capacity: {netContentPercentage:00.00}%",
                    Position = ScalePos(62.5000f, 431.4000f),
                    Color = new Color(255, 255, 255, 255),
                    FontId = "White",
                    Alignment = TextAlignment.LEFT,
                    RotationOrScale = ScaleText(0.80f),
                });

                // [2] Bar Chart for Efficiency (Helper method handles the scaling internally)
                frame.Add(CreateVerticalBar(
                    new Vector2(33.2175f, 346.7412f),
                    barSize,
                    efficiencyRatio,
                    new Color(255, 255, 255, 255)
                ));

                // [3] Bar Chart for Net Capacity (Helper method handles the scaling internally)
                Color capacityColor = capacityRatio > 0.95f ? Color.OrangeRed : Color.White;

                frame.Add(CreateVerticalBar(
                    new Vector2(33.2175f, 463.4020f),
                    barSize,
                    capacityRatio,
                    capacityColor
                ));

                var locationText = logic.Evaluator.IsInLocation ? "Yes" : "No";
                var locationColor = logic.Evaluator.IsInLocation ? Color.LightGreen : Color.Orange;

                // [10] Fishing Location Text
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = $"Fishing Location: {locationText}",
                    Position = ScalePos(12.4000f, 157.5000f),
                    Color = locationColor,
                    FontId = "White",
                    Alignment = TextAlignment.LEFT,
                    RotationOrScale = ScaleText(0.80f),
                });

                if (logic.ContentToBeLost)
                {
                    // [8] TEXT "WARNING!
                    frame.Add(new MySprite
                    {
                        Type = SpriteType.TEXT,
                        Data = "WARNING!\nNot enough inventory!",
                        Position = ScalePos(155.1000f, 464.9000f),
                        Color = new Color(255, 0, 0, 255),
                        FontId = "White",
                        Alignment = TextAlignment.CENTER,
                        RotationOrScale = ScaleText(0.60f),
                    });
                }

                // Catch break down below
                if (logic.Content?.CaughtFish != null)
                {
                    int rowIndex = 0;
                    foreach (var kvp in logic.Content.CaughtFish)
                    {
                        // Optional: Skip drawing fish types that currently have < 1 whole fish
                        if (kvp.Value < 1f) continue;

                        // Safety limit: Max 7 rows to prevent drawing off the bottom edge
                        if (rowIndex >= 7) break;

                        // Generate the sprites and add them to the frame (Helper handles scaling)
                        var rowSprites = CreateCatchRowSprites(rowIndex, kvp.Key, kvp.Value);
                        frame.AddRange(rowSprites);

                        rowIndex++;
                    }
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

        private MySprite CreateVerticalBar(Vector2 maxCenterPos, Vector2 maxSize, float fillRatio, Color color)
        {
            // Ensure the ratio never exceeds 0% to 100%
            fillRatio = MathHelper.Clamp(fillRatio, 0f, 1f);

            // Calculate the new height
            float currentHeight = maxSize.Y * fillRatio;

            // Find the absolute bottom edge of the maximum bar
            float bottomEdgeY = maxCenterPos.Y + (maxSize.Y / 2f);

            // Shift the center point down by half of the new height so it touches the bottom edge
            float newCenterY = bottomEdgeY - (currentHeight / 2f);

            return new MySprite
            {
                Type = SpriteType.TEXTURE,
                Data = "SquareSimple",
                // Wrap ONLY the final output!
                Position = ScalePos(maxCenterPos.X, newCenterY),
                Size = ScaleSize(maxSize.X, currentHeight),
                Color = color,
                Alignment = TextAlignment.CENTER
            };
        }
        private MySprite CopySprite(MySprite original, Vector2 posOffset, Vector2? sizeOverride = null)
        {
            var s = original;
            s.Position += posOffset;
            if (sizeOverride.HasValue) s.Size = sizeOverride.Value;
            return s;
        }

        private List<MySprite> CreateCatchRowSprites(int rowIndex, string subtypeId, float amount)
        {
            List<MySprite> rowSprites = new List<MySprite>();

            // The starting Y position of the first box center
            float startY = 103.2000f;
            float rowSpacing = 54.0f;
            float baseY = startY + (rowIndex * rowSpacing);

            // The text needs to be shifted up slightly from the center of the box to look vertically aligned
            float textYOffset = -15.417f;

            string displayName = FishCollectorComponent.GetDisplayName(subtypeId) ?? subtypeId;
            string amountText = Math.Floor(amount).ToString();

            // [1] Outer box for outline border effect
            rowSprites.Add(new MySprite
            {
                Type = SpriteType.TEXTURE,
                Data = "SquareSimple",
                Position = ScalePos(380.4000f, baseY),
                Size = ScaleSize(232.0000f, 50.0000f),
                Color = new Color(35, 72, 95, 255),
                Alignment = TextAlignment.CENTER
            });

            // [2] Inner box for outline 
            rowSprites.Add(new MySprite
            {
                Type = SpriteType.TEXTURE,
                Data = "SquareSimple",
                Position = ScalePos(380.4000f, baseY),
                Size = ScaleSize(228.0000f, 46.0000f),
                Color = new Color(12, 25, 33, 255),
                Alignment = TextAlignment.CENTER
            });

            // [3] Fish Icon
            rowSprites.Add(new MySprite
            {
                Type = SpriteType.TEXTURE,
                Data = "FishIcon",
                Position = ScalePos(289.7972f, baseY),
                Size = ScaleSize(44.0000f, 44.0000f),
                Color = new Color(255, 255, 255, 255),
                Alignment = TextAlignment.CENTER
            });

            // [4] Fish Display Name
            rowSprites.Add(new MySprite
            {
                Type = SpriteType.TEXT,
                Data = displayName,
                Position = ScalePos(313.2679f, baseY + textYOffset),
                Color = new Color(255, 255, 255, 255),
                FontId = "White",
                Alignment = TextAlignment.LEFT,
                RotationOrScale = ScaleText(0.900f) // Scale applied to Text!
            });

            // [5] Content amount
            rowSprites.Add(new MySprite
            {
                Type = SpriteType.TEXT,
                Data = amountText,
                Position = ScalePos(492f, baseY + textYOffset),
                Color = new Color(255, 255, 255, 255),
                FontId = "White",
                Alignment = TextAlignment.RIGHT,
                RotationOrScale = ScaleText(0.900f) // Scale applied to Text!
            });

            return rowSprites;
        }

        private Vector2 ScalePos(float x, float y)
        {
            return _canvasTopLeft + new Vector2(x * _uiScale, y * _uiScale);
        }

        private Vector2 ScaleSize(float width, float height)
        {
            return new Vector2(width * _uiScale, height * _uiScale);
        }

        private float ScaleText(float originalScale)
        {
            return originalScale * _uiScale;
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