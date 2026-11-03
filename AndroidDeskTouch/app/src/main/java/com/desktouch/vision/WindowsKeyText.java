package com.desktouch.vision;

/** Pure, testable Windows VK -> plain Android editable-field text mapping. */
final class WindowsKeyText {
    static final class Edit {
        final String text; final int cursor;
        Edit(String text,int cursor){this.text=text;this.cursor=cursor;}
    }
    private WindowsKeyText(){}
    static Edit apply(String original,int start,int end,int vk,boolean shift){
        if(original==null)original="";
        start=Math.max(0,Math.min(original.length(),start));
        end=Math.max(start,Math.min(original.length(),end));
        if(vk==37)return new Edit(original,Math.max(0,start-1)); // left
        if(vk==39)return new Edit(original,Math.min(original.length(),end+1)); // right
        if(vk==36)return new Edit(original,0); // home
        if(vk==35)return new Edit(original,original.length()); // end
        if(vk==8 && start==end)start=Math.max(0,start-1);
        if(vk==46 && start==end)end=Math.min(original.length(),end+1);
        if(vk!=8 && vk!=46) {
            String character=printable(vk,shift);
            if(character==null)return null;
            return new Edit(original.substring(0,start)+character+original.substring(end),
                start+character.length());
        }
        return new Edit(original.substring(0,start)+original.substring(end),start);
    }
    static String printable(int vk,boolean shift){
        if(vk>=65&&vk<=90)
            return String.valueOf((char)((shift?65:97)+(vk-65)));
        if(vk>=48&&vk<=57) {
            final String shifted=")!@#$%^&*(";
            return shift?String.valueOf(shifted.charAt(vk-48)):
                String.valueOf((char)vk);
        }
        if(vk>=96&&vk<=105)return String.valueOf((char)('0'+vk-96));
        if(vk==32)return " ";
        if(vk==13)return "\n";
        if(vk==9)return "\t";
        switch(vk){
            case 186:return shift?":":";";
            case 187:return shift?"+":"=";
            case 188:return shift?"<":",";
            case 189:return shift?"_":"-";
            case 190:return shift?">":".";
            case 191:return shift?"?":"/";
            case 192:return shift?"~":"`";
            case 219:return shift?"{":"[";
            case 220:return shift?"|":"\\";
            case 221:return shift?"}":"]";
            case 222:return shift?"\"":"'";
            default:return null;
        }
    }
}
