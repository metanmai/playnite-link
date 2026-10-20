using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        // Resolve active Playnite theme colours, falling back to Windows colours.
        private static Brush ThemeBrush(string key,Brush fallback)
        {return Application.Current==null?fallback:(Application.Current.TryFindResource(key) as Brush??fallback);}
        private static Brush Ink{get{return ThemeBrush("TextBrush",SystemColors.WindowTextBrush);}}
        private static Brush Muted{get{var value=ThemeBrush("TextBrushDarker",ThemeBrush("TextBrushDark",Ink));return Contrast(value,Surface)>=4.5?value:Ink;}}
        private static Brush Canvas{get{return ThemeBrush("PopupBackgroundBrush",ThemeBrush("ControlBackgroundDarkBrush",SystemColors.WindowBrush));}}
        private static Brush Surface{get{return ThemeBrush("ButtonBackgroundBrush",ThemeBrush("ControlBackgroundBrush",Canvas));}}
        private static Brush Line{get{return ThemeBrush("PopupBorderBrush",ThemeBrush("SelectionLightBrush",SystemColors.ActiveBorderBrush));}}
        private static Brush Accent{get{return ThemeBrush("HighlightGlyphBrush",ThemeBrush("GlyphBrush",SystemColors.HighlightBrush));}}
        private static void ApplyTheme(FrameworkElement element,DependencyProperty property,string key,Brush fallback)
        {if(Application.Current!=null&&Application.Current.TryFindResource(key) is Brush)element.SetResourceReference(property,key);else element.SetValue(property,fallback);}
        internal static void StyleInput(Control input)
        {input.Background=Surface;input.Foreground=Ink;input.BorderBrush=Line;}
        private static double Luminance(Brush brush)
        {var solid=brush as SolidColorBrush;if(solid==null)return 0.5;Func<byte,double> channel=value=>{var v=value/255.0;return v<=0.04045?v/12.92:Math.Pow((v+0.055)/1.055,2.4);};return 0.2126*channel(solid.Color.R)+0.7152*channel(solid.Color.G)+0.0722*channel(solid.Color.B);}
        private static double Contrast(Brush first,Brush second)
        {var a=Luminance(first);var b=Luminance(second);return (Math.Max(a,b)+0.05)/(Math.Min(a,b)+0.05);}
        private static Brush AccentText{get{return Contrast(Ink,Accent)>=Contrast(Canvas,Accent)?Ink:Canvas;}}
        private static TextBlock Label(Panel panel,string text,int size=14,bool muted=false)
        {
            var label=new TextBlock{Tag=muted,Text=text,FontSize=size,Foreground=muted?Muted:Ink,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8),LineHeight=size*1.45};if(!muted)ApplyTheme(label,TextBlock.ForegroundProperty,"TextBrush",Ink);panel.Children.Add(label);return label;
        }
        private static StackPanel Card(Panel parent,string title,string description=null)
        {
            var body=new StackPanel();var border=new Border{Background=Surface,BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(20),Margin=new Thickness(0,0,0,16),Child=body};
            ApplyTheme(border,Border.BackgroundProperty,"ButtonBackgroundBrush",Surface);ApplyTheme(border,Border.BorderBrushProperty,"PopupBorderBrush",Line);parent.Children.Add(border);Label(body,title,18).FontWeight=FontWeights.SemiBold;if(description!=null)Label(body,description,14,true);return body;
        }
        private static void StyleAction(Button button,bool primary=false)
        {
            button.FontSize=14;button.FontWeight=FontWeights.SemiBold;button.Foreground=Ink;button.Background=primary?Accent:Surface;button.BorderBrush=primary?Accent:Line;
            if(primary)button.Foreground=AccentText;else ApplyTheme(button,Control.ForegroundProperty,"TextBrush",Ink);ApplyTheme(button,Control.BackgroundProperty,primary?"HighlightGlyphBrush":"ButtonBackgroundBrush",primary?Accent:Surface);ApplyTheme(button,Control.BorderBrushProperty,primary?"HighlightGlyphBrush":"PopupBorderBrush",primary?Accent:Line);
            button.Padding=new Thickness(16,10,16,10);button.Margin=new Thickness(0,0,10,8);button.MinHeight=42;button.HorizontalAlignment=HorizontalAlignment.Left;
            var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
            var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);
            var template=new ControlTemplate(typeof(Button)){VisualTree=border};
            var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(UIElement.OpacityProperty,0.82));template.Triggers.Add(hover);
            var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Control.BorderBrushProperty,Accent));template.Triggers.Add(focus);
            var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,0.55));template.Triggers.Add(disabled);button.Template=template;
        }
        private static WrapPanel Actions(Panel parent){var row=new WrapPanel{Margin=new Thickness(0,8,0,0)};parent.Children.Add(row);return row;}
        private StackPanel Page(Window window,string title,string description,string section=null)
        {
            var body=new StackPanel{Margin=new Thickness(28)};
            if(section!=null){
                var header=new DockPanel{LastChildFill=true};var close=new Button{Content="Close"};StyleAction(close);close.Click+=(_,e)=>window.Close();DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
                header.Children.Add(new TextBlock{Text="PLAYNITE LINK",FontSize=12,Foreground=Muted,VerticalAlignment=VerticalAlignment.Center});body.Children.Add(header);
            }else Label(body,"PLAYNITE LINK",12,true);
            Label(body,title,28).FontWeight=FontWeights.SemiBold;Label(body,description,14,true);
            if(section!=null)
            {
                var nav=Actions(body);
                foreach(var name in new[]{"Overview","Devices","Protection","Add-ons"})
                {
                    var target=name;var action=Button(nav,target,()=>OpenAccountPage(target));
                    if(target==section){StyleAction(action,true);action.IsEnabled=false;}
                }
                body.Children.Add(new Border{Height=1,Background=Line,Margin=new Thickness(0,12,0,22)});
            }
            window.Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=Canvas};return body;
        }
        private static Expander Details(Panel parent,string title,UIElement content)
        {var item=new Expander{Header=title,Foreground=Muted,Content=content,Margin=new Thickness(0,8,0,8)};parent.Children.Add(item);return item;}
        private static StackPanel Footer(Window window)
        {
            var content=(UIElement)window.Content;window.Content=null;
            var body=new StackPanel();var footer=new Border{Child=body,Background=Canvas,BorderBrush=Line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(28,12,18,8)};
            var layout=new DockPanel{LastChildFill=true};DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);layout.Children.Add(content);window.Content=layout;return body;
        }
    }
}
