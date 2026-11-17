using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace AiVoiceBridge.Core;

// Format conversion pulls only the frames requested by the render clock.
// No Media Foundation transform with a large, file-oriented input read-ahead.
public sealed class LiveOutputProvider : IWaveProvider
{
    private readonly ISampleProvider _samples;
    private float[] _scratch=[];
    public WaveFormat WaveFormat { get; }
    public LiveOutputProvider(ISampleProvider source,WaveFormat target)
    {
        WaveFormat=target;
        ISampleProvider converted=new ChannelMixer(source,target.Channels);
        if(source.WaveFormat.SampleRate!=target.SampleRate)
            converted=new WdlResamplingSampleProvider(converted,target.SampleRate);
        _samples=converted;
        PcmDecoder.Validate(target);
    }
    public int Read(byte[] buffer,int offset,int count)
    {
        int bytes=WaveFormat.BitsPerSample/8;
        count-=count%WaveFormat.BlockAlign;
        int length=count/bytes;
        if(_scratch.Length<length)_scratch=new float[length];
        int read=_samples.Read(_scratch,0,length);
        var span=buffer.AsSpan(offset,count);
        bool floating=PcmDecoder.IsFloat(WaveFormat);
        for(int i=0;i<read;i++)
        {
            float value=Math.Clamp(_scratch[i],-1,1);
            int at=i*bytes;
            if(floating) BinaryPrimitives.WriteSingleLittleEndian(span.Slice(at,4),value);
            else if(bytes==2)BinaryPrimitives.WriteInt16LittleEndian(span.Slice(at,2),(short)(value*32767));
            else if(bytes==3){int pcm=(int)(value*8388607);span[at]=(byte)pcm;span[at+1]=(byte)(pcm>>8);span[at+2]=(byte)(pcm>>16);}
            else BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at,4),(int)((double)value*2147483647));
        }
        span.Slice(read*bytes).Clear();
        return count;
    }
    private sealed class ChannelMixer(ISampleProvider source,int channels) : ISampleProvider
    {
        private float[] _input=[];
        public WaveFormat WaveFormat { get; }=WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate,channels);
        public int Read(float[] output,int offset,int count)
        {
            int from=source.WaveFormat.Channels, frames=count/channels;
            if(_input.Length<frames*from)_input=new float[frames*from];
            int read=source.Read(_input,0,frames*from)/from;
            for(int f=0;f<read;f++)for(int ch=0;ch<channels;ch++)
            {
                float value;
                if(from==channels)value=_input[f*from+ch];
                else if(from==1)value=_input[f];
                else if(channels==1){value=0;for(int c=0;c<from;c++)value+=_input[f*from+c]/from;}
                else if(from>2 && channels==2)
                {
                    // Standard Windows surround ordering: FL FR FC LFE BL BR SL SR.
                    float total=_input[f*from+ch];float weight=1;
                    if(from>2){total+=_input[f*from+2]*.7071f;weight+=.7071f;}
                    for(int c=4+ch;c<from;c+=2){total+=_input[f*from+c]*.5f;weight+=.5f;}
                    value=total/weight;
                }
                else value=ch<from?_input[f*from+ch]:0;
                output[offset+f*channels+ch]=value;
            }
            return read*channels;
        }
    }
}
public static class PcmDecoder
{
    public static bool IsFloat(WaveFormat format)=>format.Encoding==WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat==new Guid("00000003-0000-0010-8000-00aa00389b71");
    public static void Validate(WaveFormat format)
    {
        bool pcm=format.Encoding==WaveFormatEncoding.Pcm || format is WaveFormatExtensible ext && ext.SubFormat==new Guid("00000001-0000-0010-8000-00aa00389b71");
        if((!IsFloat(format)&&!pcm) || format.BitsPerSample is not (16 or 24 or 32) || IsFloat(format)&&format.BitsPerSample!=32)
            throw new NotSupportedException("Неподдерживаемый PCM-формат: "+format);
    }
    public static int Decode(ReadOnlySpan<byte> source,Span<float> destination,WaveFormat format)
    {
        int bytes=format.BitsPerSample/8, count=Math.Min(source.Length/bytes,destination.Length);
        bool floating=IsFloat(format);
        for(int i=0;i<count;i++)
        {
            var part=source.Slice(i*bytes,bytes);
            destination[i]=floating?BinaryPrimitives.ReadSingleLittleEndian(part):bytes switch
            {
                2=>BinaryPrimitives.ReadInt16LittleEndian(part)/32768f,
                3=>((part[0]<<8)|(part[1]<<16)|(part[2]<<24))/2147483648f,
                _=>BinaryPrimitives.ReadInt32LittleEndian(part)/2147483648f
            };
        }
        return count;
    }
}
