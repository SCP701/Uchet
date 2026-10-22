using System;
using System.Globalization;
using System.Linq;

namespace WorkLedger
{
    // UI input only: validate grouping before removing its separators.
    internal static class NumericText
    {
        internal static bool TryParse(string value,NumberStyles styles,out decimal number)
        {
            number=0m;string text=(value??"").Trim();
            int sign=text.Length>0&&(text[0]=='+'||text[0]=='-')?1:0;
            if(sign>0&&(styles&NumberStyles.AllowLeadingSign)==0)return false;
            int separator=-1;
            for(int index=sign;index<text.Length;index++)
                if(text[index]=='.'||text[index]==',')
                {
                    if(separator>=0)return false;
                    separator=index;
                }
            string whole=text.Substring(sign,separator<0?text.Length-sign:separator-sign);
            string fraction=separator<0?"":text.Substring(separator+1);
            if(!Digits(fraction))return false;
            string[] groups=whole.Split(new[]{' ','\u00a0','\u202f'},StringSplitOptions.None);
            if(groups.Length>1)
            {
                if(groups[0].Length<1||groups[0].Length>3||!Digits(groups[0])
                    ||groups.Skip(1).Any(group=>group.Length!=3||!Digits(group)))return false;
            }
            else if(!Digits(whole))return false;
            string normalized=(sign==1?text.Substring(0,1):"")+String.Join("",groups)+(separator<0?"":"."+fraction);
            return Decimal.TryParse(normalized,styles,CultureInfo.InvariantCulture,out number);
        }
        private static bool Digits(string text)
        { return text.All(character=>character>='0'&&character<='9'); }
    }
}
