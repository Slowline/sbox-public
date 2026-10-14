HEADER
{
	DevShader = true;
}

MODES
{
	Default();
	Forward();
}

COMMON
{
	#include "system.fxc"
	#include "common.fxc"
}

struct VS_INPUT
{
	float3 position : POSITION < Semantic( PosXyz ); >;
	float2 uv : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct PS_INPUT
{
	float2 uv : TEXCOORD0;
	float4 position : SV_Position;
};

VS
{
	float4 viewport < Source( Viewport ); >;
	BoolAttribute( ScreenSpaceVertices, true );

	PS_INPUT MainVs( VS_INPUT i )
	{
		PS_INPUT o;
		float2 position = 2.0 * (i.position.xy - viewport.xy) / viewport.zw - 1.0;
		position.y *= -1.0;
		o.position = float4( position, 0.0, 1.0 );
		o.uv = i.uv;
		return o;
	}
}

PS
{
	Texture2D g_tColor < Attribute( "BrandingTexture" ); SrgbRead( true ); >;
	float opacity < Attribute( "BrandingOpacity" ); Default( 1.0 ); >;
	SamplerState samplerState < Filter( MIN_MAG_MIP_LINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );

	float4 MainPs( PS_INPUT i ) : SV_Target0
	{
		float4 color = g_tColor.Sample( samplerState, i.uv );
		color.a *= opacity;
		return color;
	}
}
