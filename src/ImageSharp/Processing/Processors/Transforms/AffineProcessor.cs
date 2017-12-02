﻿// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Helpers;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Primitives;

namespace SixLabors.ImageSharp.Processing.Processors
{
    /// <summary>
    /// Provides the base methods to perform affine transforms on an image.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    internal abstract class AffineProcessor<TPixel> : CloningImageProcessor<TPixel>
        where TPixel : struct, IPixel<TPixel>
    {
        private Rectangle targetRectangle;
        private Matrix3x2 transformMatrix;

        /// <summary>
        /// Initializes a new instance of the <see cref="AffineProcessor{TPixel}"/> class.
        /// </summary>
        /// <param name="sampler">The sampler to perform the transform operation.</param>
        protected AffineProcessor(IResampler sampler)
        {
            this.Sampler = sampler;
        }

        /// <summary>
        /// Gets the sampler to perform interpolation of the transform operation.
        /// </summary>
        public IResampler Sampler { get; }

        /// <summary>
        /// Returns the processing matrix used for transforming the image.
        /// </summary>
        /// <returns>The <see cref="Matrix3x2"/></returns>
        protected abstract Matrix3x2 GetTransformMatrix();

        /// <inheritdoc/>
        protected override Image<TPixel> CreateDestination(Image<TPixel> source, Rectangle sourceRectangle)
        {
            this.ResizeCanvas(sourceRectangle);

            // We will always be creating the clone even for mutate because we may need to resize the canvas
            IEnumerable<ImageFrame<TPixel>> frames =
                source.Frames.Select(x => new ImageFrame<TPixel>(this.targetRectangle.Width, this.targetRectangle.Height, x.MetaData.Clone()));

            // Use the overload to prevent an extra frame being added
            return new Image<TPixel>(source.GetConfiguration(), source.MetaData.Clone(), frames);
        }

        /// <inheritdoc/>
        protected override void OnApply(ImageFrame<TPixel> source, ImageFrame<TPixel> destination, Rectangle sourceRectangle, Configuration configuration)
        {
            int height = this.targetRectangle.Height;
            int width = this.targetRectangle.Width;
            Rectangle sourceBounds = source.Bounds();

            // Since could potentially be resizing the canvas we need to recenter the matrix
            Matrix3x2 matrix = this.GetCenteredMatrix(source);

            if (this.Sampler is NearestNeighborResampler)
            {
                Parallel.For(
                    0,
                    height,
                    configuration.ParallelOptions,
                    y =>
                    {
                        Span<TPixel> destRow = destination.GetPixelRowSpan(y);

                        for (int x = 0; x < width; x++)
                        {
                            var point = Point.Transform(new Point(x, y), matrix);
                            if (sourceBounds.Contains(point.X, point.Y))
                            {
                                destRow[x] = source[point.X, point.Y];
                            }
                        }
                    });

                return;
            }

            int maxSourceX = source.Width - 1;
            int maxSourceY = source.Height - 1;
            (float radius, float scale, float ratio) xRadiusScale = this.GetSamplingRadius(source.Width, destination.Width);
            (float radius, float scale, float ratio) yRadiusScale = this.GetSamplingRadius(source.Height, destination.Height);
            float xScale = xRadiusScale.scale;
            float yScale = yRadiusScale.scale;
            var radius = new Vector2(xRadiusScale.radius, yRadiusScale.radius);
            IResampler sampler = this.Sampler;
            var maxSource = new Vector4(maxSourceX, maxSourceY, maxSourceX, maxSourceY);
            int xLength = (int)MathF.Ceiling((radius.X * 2) + 2);
            int yLength = (int)MathF.Ceiling((radius.Y * 2) + 2);

            Parallel.For(
                0,
                height,
                configuration.ParallelOptions,
                y =>
                {
                    Span<TPixel> destRow = destination.GetPixelRowSpan(y);
                    using (var yBuffer = new Buffer<float>(yLength))
                    using (var xBuffer = new Buffer<float>(xLength))
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // Use the single precision position to calculate correct bounding pixels
                            // otherwise we get rogue pixels outside of the bounds.
                            var point = Vector2.Transform(new Vector2(x, y), matrix);

                            // Clamp sampling pixel radial extents to the source image edges
                            Vector2 maxXY = point + radius;
                            Vector2 minXY = point - radius;

                            var extents = new Vector4(
                                MathF.Ceiling(maxXY.X),
                                MathF.Ceiling(maxXY.Y),
                                MathF.Floor(minXY.X),
                                MathF.Floor(minXY.Y));

                            extents = Vector4.Clamp(extents, Vector4.Zero, maxSource);

                            int maxX = (int)extents.X;
                            int maxY = (int)extents.Y;
                            int minX = (int)extents.Z;
                            int minY = (int)extents.W;

                            // TODO: Find a way to speed this up if we can we precalculated weights!!!
                            // It appears these have to be calculated on-the-fly.
                            // Check with Anton to figure out why indexing from the precalculated weights was wrong.
                            //
                            // Create and normalize the y-weights
                            float ySum = 0;
                            for (int yy = 0, i = minY; i <= maxY; i++, yy++)
                            {
                                float weight = sampler.GetValue((i - point.Y) / yScale);
                                ySum += weight;
                                yBuffer[yy] = weight;
                            }

                            // TODO:
                            // Normalizing the weights fixes scaled transfrom where we scale down but breaks edge pixel belnding
                            // We end up with too much weight on pixels that should be blended.
                            // We could, maybe, move the division into the loop and not divide when we hit 0 or maxN but that seems clunky.
                            if (ySum > 0)
                            {
                                for (int i = 0; i < yBuffer.Length; i++)
                                {
                                    yBuffer[i] = yBuffer[i] / ySum;
                                }
                            }

                            // Create and normalize the x-weights
                            float xSum = 0;
                            for (int xx = 0, i = minX; i <= maxX; i++, xx++)
                            {
                                float weight = sampler.GetValue((i - point.X) / xScale);
                                xSum += weight;
                                xBuffer[xx] = weight;
                            }

                            if (xSum > 0)
                            {
                                for (int i = 0; i < xBuffer.Length; i++)
                                {
                                    xBuffer[i] = xBuffer[i] / xSum;
                                }
                            }

                            // Now multiply the normalized results against the offsets
                            Vector4 sum = Vector4.Zero;
                            for (int yy = 0, j = minY; j <= maxY; j++, yy++)
                            {
                                float yWeight = yBuffer[yy];

                                for (int xx = 0, i = minX; i <= maxX; i++, xx++)
                                {
                                    float xWeight = xBuffer[xx];
                                    sum += source[i, j].ToVector4() * xWeight * yWeight;
                                }
                            }

                            ref TPixel dest = ref destRow[x];
                            dest.PackFromVector4(sum);
                        }
                    }
                });
        }

        /// <summary>
        /// Gets a transform matrix adjusted to center upon the target image bounds.
        /// </summary>
        /// <param name="source">The source image.</param>
        /// <returns>
        /// The <see cref="Matrix3x2"/>.
        /// </returns>
        protected Matrix3x2 GetCenteredMatrix(ImageFrame<TPixel> source)
        {
            var translationToTargetCenter = Matrix3x2.CreateTranslation(-this.targetRectangle.Width * .5F, -this.targetRectangle.Height * .5F);
            var translateToSourceCenter = Matrix3x2.CreateTranslation(source.Width * .5F, source.Height * .5F);
            return translationToTargetCenter * this.transformMatrix * translateToSourceCenter;
        }

        /// <summary>
        /// Creates a new target canvas to contain the results of the matrix transform.
        /// </summary>
        /// <param name="sourceRectangle">The source rectangle.</param>
        private void ResizeCanvas(Rectangle sourceRectangle)
        {
            this.transformMatrix = this.GetTransformMatrix();
            this.targetRectangle = Matrix3x2.Invert(this.transformMatrix, out Matrix3x2 sizeMatrix)
                                       ? ImageMaths.GetBoundingRectangle(sourceRectangle, sizeMatrix)
                                       : sourceRectangle;
        }

        /// <summary>
        /// Calculates the sampling radius for the current sampler
        /// </summary>
        /// <param name="sourceSize">The source dimension size</param>
        /// <param name="destinationSize">The destination dimension size</param>
        /// <returns>The radius, and scaling factor</returns>
        private (float radius, float scale, float ratio) GetSamplingRadius(int sourceSize, int destinationSize)
        {
            float ratio = (float)sourceSize / destinationSize;
            float scale = ratio;

            if (scale < 1F)
            {
                scale = 1F;
            }

            return (MathF.Ceiling(scale * this.Sampler.Radius), scale, ratio);
        }
    }
}