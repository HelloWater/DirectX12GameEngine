﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace DirectX12GameEngine
{
    public sealed class RenderSystem : EntitySystem<ModelComponent>
    {
        private readonly List<CommandList> commandLists = new List<CommandList>();

        public RenderSystem(IServiceProvider services) : base(services, typeof(TransformComponent))
        {
            SceneSystem = Services.GetRequiredService<SceneSystem>();

            Span<Matrix4x4> matrices = stackalloc Matrix4x4[] { Matrix4x4.Identity, Matrix4x4.Identity };
            ViewProjectionBuffer = Texture.CreateConstantBufferView(GraphicsDevice, matrices);
        }

        public SceneSystem SceneSystem { get; }

        public Texture ViewProjectionBuffer { get; }

        public override void Draw(TimeSpan deltaTime)
        {
            if (GraphicsDevice.Presenter is null) return;

            UpdateViewProjectionMatrices();

            var componentsWithSameModel = Components.GroupBy(m => m.Model).ToArray();

            int batchCount = Math.Min(Environment.ProcessorCount, componentsWithSameModel.Length);
            int batchSize = (int)Math.Ceiling((double)componentsWithSameModel.Length / batchCount);

            for (int i = commandLists.Count; i < batchCount; i++)
            {
                CommandList commandList = new CommandList(GraphicsDevice, SharpDX.Direct3D12.CommandListType.Direct);
                commandList.Close();

                commandLists.Add(commandList);
            }

            CompiledCommandList[] compiledCommandLists = new CompiledCommandList[batchCount];

            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++) /*Parallel.For(0, batchCount, batchIndex =>*/
            {
                CommandList commandList = commandLists[batchIndex];
                commandList.Reset();

                commandList.SetViewport(GraphicsDevice.Presenter.Viewport);
                commandList.SetScissorRectangles(GraphicsDevice.Presenter.ScissorRect);
                commandList.SetRenderTargets(GraphicsDevice.Presenter.DepthStencilBuffer, GraphicsDevice.Presenter.BackBuffer);

                int end = Math.Min((batchIndex * batchSize) + batchSize, componentsWithSameModel.Length);

                for (int i = batchIndex * batchSize; i < end; i++)
                {
                    Model? model = componentsWithSameModel[i].Key;
                    var modelComponents = componentsWithSameModel[i];

                    if (model is null) continue;

                    int meshCount = model.Meshes.Count;

                    if (model.ConstantBuffers is null || model.ConstantBuffers.Length != meshCount)
                    {
                        model.CommandList?.Builder.Dispose();
                        model.CommandList = null;

                        if (model.ConstantBuffers != null)
                        {
                            foreach (Texture constantBuffer in model.ConstantBuffers) constantBuffer?.Dispose();
                        }

                        model.ConstantBuffers = new Texture[meshCount];

                        for (int j = 0; j < meshCount; j++)
                        {
                            model.ConstantBuffers[j] = Texture.CreateConstantBufferView(GraphicsDevice, modelComponents.Count() * 16 * sizeof(float)).DisposeBy(GraphicsDevice);
                        }
                    }

                    int modelComponentIndex = 0;

                    foreach (ModelComponent modelComponent in modelComponents)
                    {
                        if (modelComponent.Entity is null)
                        {
                            modelComponentIndex++;
                            continue;
                        }

                        for (int j = 0; j < meshCount; j++)
                        {
                            Matrix4x4 worldMatrix = model.Meshes[j].WorldMatrix * modelComponent.Entity.Transform.WorldMatrix;
                            SharpDX.Utilities.Write(model.ConstantBuffers[j].MappedResource + modelComponentIndex * 16 * sizeof(float), ref worldMatrix);
                        }

                        modelComponentIndex++;
                    }

                    model.CommandList = model.CommandList ?? RecordCommandList(
                        model,
                        new CommandList(GraphicsDevice, SharpDX.Direct3D12.CommandListType.Bundle).DisposeBy(GraphicsDevice),
                        modelComponents.Count());

                    // Without bundles:
                    //RecordCommandList(model, commandList, modelComponents.Count());

                    if (model.CommandList != null && model.CommandList.Builder.CommandListType == SharpDX.Direct3D12.CommandListType.Bundle)
                    {
                        commandList.ExecuteBundle(model.CommandList);
                    }
                }

                compiledCommandLists[batchIndex] = commandList.Close();
            }

            GraphicsDevice.CommandList.Flush();
            GraphicsDevice.ExecuteCommandLists(compiledCommandLists);

            GraphicsDevice.CommandList.Reset();

            GraphicsDevice.CommandList.SetViewport(GraphicsDevice.Presenter.Viewport);
            GraphicsDevice.CommandList.SetScissorRectangles(GraphicsDevice.Presenter.ScissorRect);
            GraphicsDevice.CommandList.SetRenderTargets(GraphicsDevice.Presenter.DepthStencilBuffer, GraphicsDevice.Presenter.BackBuffer);
        }

        public override void Dispose()
        {
            ViewProjectionBuffer.Dispose();

            foreach (CommandList commandList in commandLists)
            {
                commandList.Dispose();
            }
        }

        private CompiledCommandList? RecordCommandList(Model model, CommandList commandList, int instanceCount)
        {
            if (model is null) throw new ArgumentException("The model of the model component can't be null.");
            if (model.ConstantBuffers is null) throw new ArgumentException("The constant buffers of the model component can't be null.");

            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Mesh mesh = model.Meshes[i];

                if (mesh.VertexBufferViews is null) throw new ArgumentException("The vertex buffer views of the mesh can't be null.");

                Material material = model.Materials[mesh.MaterialIndex];

                commandList.SetPipelineState(material.PipelineState);
                commandList.SetGraphicsRootDescriptorTable(0, ViewProjectionBuffer.NativeGpuDescriptorHandle);
                commandList.SetGraphicsRootDescriptorTable(1, model.ConstantBuffers[i].NativeGpuDescriptorHandle);

                for (int j = 0; j < material.Textures.Count; j++)
                {
                    commandList.SetGraphicsRootDescriptorTable(j + 2, material.Textures[j].NativeGpuDescriptorHandle);
                }

                commandList.SetIndexBuffer(mesh.IndexBufferView);
                commandList.SetVertexBuffers(mesh.VertexBufferViews);

                instanceCount *= GraphicsDevice.Presenter is null ? 1 : GraphicsDevice.Presenter.PresentationParameters.Stereo ? 2 : 1;

                if (mesh.IndexBufferView.HasValue)
                {
                    commandList.DrawIndexedInstanced(mesh.IndexBufferView.Value.SizeInBytes / SharpDX.DXGI.FormatHelper.SizeOfInBytes(mesh.IndexBufferView.Value.Format), instanceCount);
                }
                else
                {
                    commandList.DrawInstanced(mesh.VertexBufferViews[0].SizeInBytes / mesh.VertexBufferViews[0].StrideInBytes, instanceCount);
                }
            }

            return commandList.CommandListType == SharpDX.Direct3D12.CommandListType.Bundle ? commandList.Close() : null;
        }

        private void UpdateViewProjectionMatrices()
        {
            if (SceneSystem.CurrentCamera != null && SceneSystem.CurrentCamera.Entity != null)
            {
                if (GraphicsDevice.Presenter is HolographicGraphicsPresenter graphicsPresenter)
                {
                    var cameraPose = graphicsPresenter.HolographicFrame.CurrentPrediction.CameraPoses[0];

                    cameraPose.HolographicCamera.SetNearPlaneDistance(SceneSystem.CurrentCamera.NearPlaneDistance);
                    cameraPose.HolographicCamera.SetFarPlaneDistance(SceneSystem.CurrentCamera.FarPlaneDistance);

                    var viewTransform = cameraPose.TryGetViewTransform(graphicsPresenter.SpatialStationaryFrameOfReference.CoordinateSystem);

                    Span<Matrix4x4> viewMatrices = stackalloc Matrix4x4[] { Matrix4x4.Identity, Matrix4x4.Identity };
                    Span<Matrix4x4> projectionMatrices = stackalloc Matrix4x4[] { Matrix4x4.Identity, Matrix4x4.Identity };

                    if (viewTransform.HasValue)
                    {
                        Matrix4x4.Decompose(SceneSystem.CurrentCamera.Entity.Transform.WorldMatrix, out _,
                            out Quaternion rotation,
                            out Vector3 translation);

                        Matrix4x4 positionMatrix = Matrix4x4.CreateTranslation(-translation) * Matrix4x4.CreateFromQuaternion(rotation);

                        viewMatrices[0] = positionMatrix * viewTransform.Value.Left;
                        viewMatrices[1] = positionMatrix * viewTransform.Value.Right;
                    }

                    projectionMatrices[0] = cameraPose.ProjectionTransform.Left;
                    projectionMatrices[1] = cameraPose.ProjectionTransform.Right;

                    Span<Matrix4x4> matrices = stackalloc Matrix4x4[]
                    {
                        viewMatrices[0] * projectionMatrices[0],
                        viewMatrices[1] * projectionMatrices[1]
                    };

                    SharpDX.Utilities.Write(ViewProjectionBuffer.MappedResource, matrices.ToArray(), 0, matrices.Length);
                }
                else
                {
                    Matrix4x4 viewProjectionMatrix = SceneSystem.CurrentCamera.ViewProjectionMatrix;
                    Span<Matrix4x4> matrices = stackalloc Matrix4x4[] { viewProjectionMatrix, viewProjectionMatrix };
                    SharpDX.Utilities.Write(ViewProjectionBuffer.MappedResource, matrices.ToArray(), 0, matrices.Length);
                }
            }
        }
    }
}
