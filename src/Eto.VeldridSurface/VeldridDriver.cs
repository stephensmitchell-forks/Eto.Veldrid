﻿using Eto.Forms;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Veldrid.SPIRV;

namespace PlaceholderName
{
	public struct VertexPositionColor
	{
		public static uint SizeInBytes = (uint)Marshal.SizeOf(typeof(VertexPositionColor));

		public Vector2 Position;
		public RgbaFloat Color;

		public VertexPositionColor(Vector2 position, RgbaFloat color)
		{
			Position = position;
			Color = color;
		}
	}

	/// <summary>
	/// A class that controls rendering to a VeldridSurface.
	/// </summary>
	/// <remarks>
	/// VeldridSurface is only a basic control that lets you render to the screen
	/// using Veldrid. How exactly to do that is up to you; this driver class is
	/// only one possible approach, and in all likelihood not the most efficient.
	/// </remarks>
	public class VeldridDriver
	{
		private VeldridSurface _surface;
		public VeldridSurface Surface
		{
			get { return _surface; }
			set
			{
				_surface = value;

				Surface.MouseDown += Surface_MouseDown;
				Surface.KeyDown += Surface_KeyDown;
			}
		}

		private void Surface_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Keys.C)
			{
				Clockwise = !Clockwise;
				e.Handled = true;
			}
		}

		private void Surface_MouseDown(object sender, MouseEventArgs e)
		{
			if (e.Buttons == MouseButtons.Primary)
			{
				Animate = !Animate;
				e.Handled = true;
			}
		}

		public UITimer Clock = new UITimer();

		public CommandList CommandList;
		public DeviceBuffer VertexBuffer;
		public DeviceBuffer IndexBuffer;
		public Shader VertexShader;
		public Shader FragmentShader;
		public Pipeline Pipeline;

		public Matrix4x4 ModelMatrix = Matrix4x4.Identity;
		public DeviceBuffer ModelBuffer;
		public ResourceSet ModelMatrixSet;

		public bool Animate { get; set; } = true;

		private int _direction = 1;
		public bool Clockwise
		{
			get { return _direction == 1 ? true : false; }
			set { _direction = value ? 1 : -1; }
		}

		private bool Ready = false;

		public VeldridDriver()
		{
			Clock.Interval = 1.0f / 60.0f;
			Clock.Elapsed += Clock_Elapsed;
		}

		private void Clock_Elapsed(object sender, EventArgs e)
		{
			Draw();
		}

		private DateTime CurrentTime;
		private DateTime PreviousTime = DateTime.Now;

		public void Draw()
		{
			if (!Ready)
			{
				return;
			}

			CommandList.Begin();

			CurrentTime = DateTime.Now;
			if (Animate)
			{
				ModelMatrix *= Matrix4x4.CreateFromAxisAngle(
					new Vector3(0, 0, _direction),
					OpenTK.MathHelper.DegreesToRadians(Convert.ToSingle((CurrentTime - PreviousTime).TotalMilliseconds / 10.0)));
			}
			PreviousTime = CurrentTime;
			CommandList.UpdateBuffer(ModelBuffer, 0, ModelMatrix);

			CommandList.SetFramebuffer(Surface.Swapchain.Framebuffer);

			// These commands differ from the stock Veldrid "Getting Started"
			// tutorial in two ways. First, the viewport is cleared to pink
			// instead of black so as to more easily distinguish between errors
			// in creating a graphics context and errors drawing vertices within
			// said context. Second, this project creates its swapchain with a
			// depth buffer, and that buffer needs to be reset at the start of
			// each frame.
			CommandList.ClearColorTarget(0, RgbaFloat.Pink);
			CommandList.ClearDepthStencil(1.0f);

			CommandList.SetVertexBuffer(0, VertexBuffer);
			CommandList.SetIndexBuffer(IndexBuffer, IndexFormat.UInt16);
			CommandList.SetPipeline(Pipeline);
			CommandList.SetGraphicsResourceSet(0, ModelMatrixSet);

			CommandList.DrawIndexed(
				indexCount: 4,
				instanceCount: 1,
				indexStart: 0,
				vertexOffset: 0,
				instanceStart: 0);

			CommandList.End();

			Surface.GraphicsDevice.SubmitCommands(CommandList);
			Surface.GraphicsDevice.SwapBuffers(Surface.Swapchain);
		}

		public void SetUpVeldrid()
		{
			CreateResources();

			Ready = true;
		}

		private void CreateResources()
		{
			ResourceFactory factory = Surface.GraphicsDevice.ResourceFactory;

			ResourceLayout modelMatrixLayout = factory.CreateResourceLayout(
				new ResourceLayoutDescription(
					new ResourceLayoutElementDescription(
						"ModelMatrix",
						ResourceKind.UniformBuffer,
						ShaderStages.Vertex)));

			ModelBuffer = factory.CreateBuffer(
				new BufferDescription(64, BufferUsage.UniformBuffer));

			ModelMatrixSet = factory.CreateResourceSet(new ResourceSetDescription(
				modelMatrixLayout, ModelBuffer));

			VertexPositionColor[] quadVertices =
			{
				new VertexPositionColor(new Vector2(-.75f, -.75f), RgbaFloat.Red),
				new VertexPositionColor(new Vector2(.75f, -.75f), RgbaFloat.Green),
				new VertexPositionColor(new Vector2(-.75f, .75f), RgbaFloat.Blue),
				new VertexPositionColor(new Vector2(.75f, .75f), RgbaFloat.Yellow)
			};

			ushort[] quadIndices = { 0, 1, 2, 3 };

			VertexBuffer = factory.CreateBuffer(new BufferDescription(4 * VertexPositionColor.SizeInBytes, BufferUsage.VertexBuffer));
			IndexBuffer = factory.CreateBuffer(new BufferDescription(4 * sizeof(ushort), BufferUsage.IndexBuffer));

			Surface.GraphicsDevice.UpdateBuffer(VertexBuffer, 0, quadVertices);
			Surface.GraphicsDevice.UpdateBuffer(IndexBuffer, 0, quadIndices);

			// Veldrid.SPIRV, when cross-compiling to HLSL, will always produce
			// TEXCOORD semantics; VertexElementSemantic.TextureCoordinate thus
			// becomes necessary to let D3D11 work alongside Vulkan and OpenGL.
			//
			//   https://github.com/mellinoe/veldrid/issues/121
			//
			var vertexLayout = new VertexLayoutDescription(
				new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
				new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

			// Veldrid.SPIRV is an additional library that complements Veldrid
			// by simplifying the development of cross-backend shaders, and is
			// currently the recommended approach to doing so:
			//
			//   https://veldrid.dev/articles/portable-shaders.html
			//
			// If you decide against using it, you can try out Veldrid developer
			// mellinoe's other project, ShaderGen, or drive yourself crazy by
			// writing and maintaining custom shader code for each platform.
			byte[] vertexShaderSpirvBytes = LoadSpirvBytes(ShaderStages.Vertex);
			byte[] fragmentShaderSpirvBytes = LoadSpirvBytes(ShaderStages.Fragment);

			var options = new CrossCompileOptions();
			switch (Surface.GraphicsDevice.BackendType)
			{
				// InvertVertexOutputY and FixClipSpaceZ address two major
				// differences between Veldrid's various graphics APIs, as
				// discussed here:
				//
				//   https://veldrid.dev/articles/backend-differences.html
				//
				// Note that the only reason those options are useful in this
				// example project is that the vertices being drawn are stored
				// the way Vulkan stores vertex data. The options will therefore
				// properly convert from the Vulkan style to whatever's used by
				// the destination backend. If you store vertices in a different
				// coordinate system, these may not do anything for you, and
				// you'll need to handle the difference in your shader code.
				case GraphicsBackend.Metal:
					options.InvertVertexOutputY = true;
					break;
				case GraphicsBackend.Direct3D11:
					options.InvertVertexOutputY = true;
					break;
				case GraphicsBackend.OpenGL:
					options.FixClipSpaceZ = true;
					options.InvertVertexOutputY = true;
					break;
				default:
					break;
			}
				
			var vertex = new ShaderDescription(ShaderStages.Vertex, vertexShaderSpirvBytes, "main", true);
			var fragment = new ShaderDescription(ShaderStages.Fragment, fragmentShaderSpirvBytes, "main", true);
			Shader[] shaders = factory.CreateFromSpirv(vertex, fragment, options);

			Pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
			{
				BlendState = BlendStateDescription.SingleOverrideBlend,
				DepthStencilState = new DepthStencilStateDescription(
					depthTestEnabled: true,
					depthWriteEnabled: true,
					comparisonKind: ComparisonKind.LessEqual),
				RasterizerState = new RasterizerStateDescription(
					cullMode: FaceCullMode.Back,
					fillMode: PolygonFillMode.Solid,
					frontFace: FrontFace.Clockwise,
					depthClipEnabled: true,
					scissorTestEnabled: false),
				PrimitiveTopology = PrimitiveTopology.TriangleStrip,
				ResourceLayouts = new[] { modelMatrixLayout },
				ShaderSet = new ShaderSetDescription(
					vertexLayouts: new VertexLayoutDescription[] { vertexLayout },
					shaders: shaders),
				Outputs = Surface.Swapchain.Framebuffer.OutputDescription
			});

			CommandList = factory.CreateCommandList();
		}

		private byte[] LoadSpirvBytes(ShaderStages stage)
		{
			byte[] bytes;

			string shaderDir = Path.Combine(AppContext.BaseDirectory, "shaders");
			string name = $"VertexColor-{stage.ToString().ToLower()}.450.glsl";
			string full = Path.Combine(shaderDir, name);

			// Precompiled SPIR-V bytecode can speed up program start by saving
			// the need to load text files and compile them before converting
			// the result to the final backend shader format. If they're not
			// available, though, the plain .glsl files will do just fine. Look
			// up glslangValidator to learn how to compile SPIR-V binary files.
			try
			{
				bytes = File.ReadAllBytes($"{full}.spv");
			}
			catch (FileNotFoundException)
			{
				bytes = File.ReadAllBytes(full);
			}

			return bytes;
		}
	}
}
