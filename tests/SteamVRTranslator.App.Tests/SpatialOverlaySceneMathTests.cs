using System.Numerics;
using SteamVRTranslator.App.SteamVR;
using Valve.VR;
using Vortice.D3DCompiler;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SpatialOverlaySceneMathTests
{
    [Fact]
    public void StereoSceneShadersCompileForD3D11()
    {
        var vertexShader = Compiler.Compile(
            SpatialOverlaySceneRenderer.ShaderSource,
            "VSMain",
            "SpatialOverlayScene.hlsl",
            "vs_5_0",
            ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor,
            EffectFlags.None);
        var pixelShader = Compiler.Compile(
            SpatialOverlaySceneRenderer.ShaderSource,
            "PSMain",
            "SpatialOverlayScene.hlsl",
            "ps_5_0",
            ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor,
            EffectFlags.None);

        Assert.False(vertexShader.IsEmpty);
        Assert.False(pixelShader.IsEmpty);
    }

    [Fact]
    public void MultiplyComposesEyeToHeadAndHeadToWorldTransforms()
    {
        var worldFromHead = Translation(1f, 2f, 3f);
        var headFromEye = Translation(-0.03f, 0.01f, 0.02f);

        var worldFromEye = SpatialOverlaySceneRenderer.Multiply(worldFromHead, headFromEye);

        Assert.Equal(0.97f, worldFromEye.m3, 4);
        Assert.Equal(2.01f, worldFromEye.m7, 4);
        Assert.Equal(3.02f, worldFromEye.m11, 4);
    }

    [Fact]
    public void Matrix34ConversionPreservesOpenVrPointTransform()
    {
        var openVr = new HmdMatrix34_t
        {
            m0 = 0f,
            m1 = -1f,
            m2 = 0f,
            m3 = 4f,
            m4 = 1f,
            m5 = 0f,
            m6 = 0f,
            m7 = 5f,
            m8 = 0f,
            m9 = 0f,
            m10 = 1f,
            m11 = 6f
        };

        var converted = SpatialOverlaySceneRenderer.ToNumerics(openVr);
        var transformed = Vector3.Transform(new Vector3(2f, 3f, 4f), converted);

        Assert.Equal(new Vector3(1f, 7f, 10f), transformed);
    }

    [Fact]
    public void Matrix44ConversionTransposesOpenVrColumnVectorLayout()
    {
        var openVr = new HmdMatrix44_t
        {
            m0 = 1f,
            m1 = 2f,
            m2 = 3f,
            m3 = 4f,
            m4 = 5f,
            m5 = 6f,
            m6 = 7f,
            m7 = 8f,
            m8 = 9f,
            m9 = 10f,
            m10 = 11f,
            m11 = 12f,
            m12 = 13f,
            m13 = 14f,
            m14 = 15f,
            m15 = 16f
        };

        var converted = SpatialOverlaySceneRenderer.ToNumerics(openVr);

        Assert.Equal(5f, converted.M12);
        Assert.Equal(2f, converted.M21);
        Assert.Equal(13f, converted.M14);
        Assert.Equal(4f, converted.M41);
    }

    private static HmdMatrix34_t Translation(float x, float y, float z) => new()
    {
        m0 = 1f,
        m5 = 1f,
        m10 = 1f,
        m3 = x,
        m7 = y,
        m11 = z
    };
}
