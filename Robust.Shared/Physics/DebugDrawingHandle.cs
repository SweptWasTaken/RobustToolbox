﻿using System;
using System.Collections.Generic;
using System.Text;
using Robust.Shared.Maths;

namespace Robust.Shared.Physics
{
    public abstract class DebugDrawingHandle
    {
        public abstract Color GridFillColor { get; }
        public abstract Color RectFillColor { get; }

        public abstract void DrawRect(in Box2 box, in Color color);

        public abstract void SetTransform(in Matrix3 transform);
    }
}