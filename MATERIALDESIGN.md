# Material Design Best Practices for WreckfestController

This document describes the Material Design implementation and best practices used in this WPF application.

**Last Updated:** November 2025
**MaterialDesignInXamlToolkit Version:** 5.3.0

---

## Table of Contents

1. [Philosophy](#philosophy)
2. [Package Installation](#package-installation)
3. [Theme Setup](#theme-setup)
4. [Button Styles](#button-styles)
5. [DataGrid Styling](#datagrid-styling)
6. [Best Practices](#best-practices)
7. [Common Patterns](#common-patterns)
8. [Resources](#resources)

---

## Philosophy

### Why Material Design?

We use Material Design to avoid creating custom UI hacks and instead rely on professional, battle-tested design patterns from Google's Material Design team.

**Benefits:**
- ✅ Professional appearance without custom CSS/XAML hacks
- ✅ Consistent design language across the application
- ✅ Automatic dark theme support
- ✅ Proper disabled state handling
- ✅ Accessibility built-in (contrast ratios, focus indicators)
- ✅ Community-maintained and well-documented

**Key Principle:**
> "Use official Material Design components instead of rolling our own - blame Google for design issues, not us!"

---

## Package Installation

### NuGet Package

```bash
dotnet add package MaterialDesignThemes
```

This installs:
- **MaterialDesignThemes** (5.3.0) - Core Material Design components
- **MaterialDesignColors** (5.3.0) - Color palettes
- **Microsoft.Xaml.Behaviors.Wpf** (1.1.77) - Behavior support

---

## Theme Setup

### App.xaml Configuration

```xml
<Application x:Class="WreckfestController.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <!-- Material Design -->
                <materialDesign:BundledTheme BaseTheme="Dark"
                                           PrimaryColor="Blue"
                                           SecondaryColor="Cyan" />
                <ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml" />
            </ResourceDictionary.MergedDictionaries>

            <!-- Custom colors for backward compatibility (optional) -->
            <SolidColorBrush x:Key="BackgroundDark" Color="#0D1117"/>
            <SolidColorBrush x:Key="TextPrimary" Color="#C9D1D9"/>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

### Theme Options

**BaseTheme:** `"Light"` or `"Dark"`
**PrimaryColor:** `Blue`, `Red`, `Green`, `Purple`, `Orange`, etc.
**SecondaryColor:** `Cyan`, `Lime`, `Amber`, `Pink`, etc.

See: [MaterialDesignInXamlToolkit Color Swatches](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/wiki/Swatches-and-Recommended-Colors)

---

## Button Styles

### Available Button Styles

Material Design provides several button styles. Use these instead of custom `Background`/`Foreground` properties:

1. **MaterialDesignRaisedButton** - Elevated button (Primary color)
2. **MaterialDesignRaisedSecondaryButton** - Elevated button (Secondary/Accent color)
3. **MaterialDesignFlatButton** - Flat button (no elevation)
4. **MaterialDesignToolButton** - Tool button style
5. **MaterialDesignFloatingActionButton** - FAB (circular, elevated)

### Best Practice: Use Official Styles

❌ **Don't do this (custom styling):**
```xml
<Button Content="Submit"
        Background="#51CF66"
        Foreground="White"
        Padding="5"
        FontWeight="Bold"/>
```

✅ **Do this (Material Design style):**
```xml
<Button Content="SUBMIT"
        Style="{StaticResource MaterialDesignRaisedSecondaryButton}"
        Width="100"/>
```

### Button Text Convention

Material Design uses **UPPERCASE** text for buttons by default. Follow this convention:

```xml
<Button Content="START" Style="{StaticResource MaterialDesignRaisedSecondaryButton}"/>
<Button Content="STOP" Style="{StaticResource MaterialDesignRaisedButton}"/>
<Button Content="REFRESH" Style="{StaticResource MaterialDesignRaisedButton}"/>
```

### Disabled State Handling

Material Design automatically handles disabled states. **Never** write custom disabled styling:

❌ **Don't do this:**
```xml
<Button.Style>
    <Style TargetType="Button">
        <Style.Triggers>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="#888888"/>
                <Setter Property="Opacity" Value="0.5"/>
            </Trigger>
        </Style.Triggers>
    </Style>
</Button.Style>
```

✅ **Do this (let Material Design handle it):**
```xml
<Button Content="ATTACH"
        Style="{StaticResource MaterialDesignRaisedSecondaryButton}"
        IsEnabled="False"/>
```

Material Design will automatically:
- Dim the button appropriately
- Maintain proper contrast ratios
- Show correct visual feedback

### Example: Status Tab Buttons

```xml
<StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
    <Button x:Name="RefreshButton"
            Content="REFRESH"
            Style="{StaticResource MaterialDesignRaisedButton}"
            Click="OnRefreshClicked"
            Width="110"
            Margin="0,0,10,0"/>

    <Button x:Name="AttachButton"
            Content="ATTACH"
            Style="{StaticResource MaterialDesignRaisedSecondaryButton}"
            Click="OnAttachClicked"
            Width="110"
            Margin="0,0,10,0"
            IsEnabled="False"/>

    <Button x:Name="KillButton"
            Content="KILL PROCESS"
            Style="{StaticResource MaterialDesignRaisedButton}"
            Click="OnKillClicked"
            Width="130"
            IsEnabled="False"/>
</StackPanel>
```

**Notes:**
- Primary actions use `MaterialDesignRaisedSecondaryButton` (accent color)
- Secondary actions use `MaterialDesignRaisedButton` (primary color)
- Destructive actions also use `MaterialDesignRaisedButton` (rely on context, not color)

---

## DataGrid Styling

Material Design provides automatic DataGrid styling, but you can customize further if needed.

### Basic Dark Theme DataGrid

```xml
<DataGrid x:Name="ProcessListGrid"
          AutoGenerateColumns="False"
          IsReadOnly="True"
          SelectionMode="Single"
          Background="{StaticResource BackgroundDark}"
          Foreground="{StaticResource TextPrimary}"
          GridLinesVisibility="Horizontal"
          HeadersVisibility="Column"
          RowBackground="{StaticResource BackgroundDark}"
          AlternatingRowBackground="{StaticResource BackgroundMedium}"
          BorderBrush="{StaticResource BorderColor}"
          BorderThickness="1">

    <!-- Column Header Style -->
    <DataGrid.ColumnHeaderStyle>
        <Style TargetType="DataGridColumnHeader">
            <Setter Property="Background" Value="{StaticResource BackgroundMedium}"/>
            <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/>
            <Setter Property="FontWeight" Value="Bold"/>
            <Setter Property="Padding" Value="8,4"/>
        </Style>
    </DataGrid.ColumnHeaderStyle>

    <!-- Cell Style -->
    <DataGrid.CellStyle>
        <Style TargetType="DataGridCell">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/>
            <Setter Property="Padding" Value="8,4"/>
        </Style>
    </DataGrid.CellStyle>

    <DataGrid.Columns>
        <DataGridTextColumn Header="PID" Binding="{Binding ProcessId}" Width="80"/>
        <DataGridTextColumn Header="Config File" Binding="{Binding ConfigFile}" Width="*"/>
        <DataGridTextColumn Header="Uptime" Binding="{Binding UptimeString}" Width="120"/>
        <DataGridTextColumn Header="Memory (MB)" Binding="{Binding MemoryUsageMB}" Width="120"/>
        <DataGridTextColumn Header="Status" Binding="{Binding StatusString}" Width="100"/>
    </DataGrid.Columns>
</DataGrid>
```

### Why Manual DataGrid Styling?

Material Design doesn't have comprehensive DataGrid styling out of the box, so we use a hybrid approach:
- Material Design for buttons, text fields, etc.
- Custom (but minimal) styling for DataGrid to match dark theme

---

## Best Practices

### 1. Prefer Material Design Styles Over Custom Styling

Always check if Material Design has a built-in style before creating custom XAML.

**Official Documentation:** [MaterialDesignInXamlToolkit Wiki](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/wiki)

### 2. Use StaticResource, Not DynamicResource

For theme colors, use `StaticResource` for better performance:

```xml
<Button Style="{StaticResource MaterialDesignRaisedButton}"/>
```

### 3. Follow Material Design Conventions

- **Button Text:** UPPERCASE
- **Spacing:** Use Material Design's default margins (no need to set Padding)
- **Elevation:** Let Material Design handle shadows and elevation
- **Colors:** Use theme colors (Primary, Secondary) instead of hardcoded hex values

### 4. Don't Override Default Styles

Material Design buttons come with:
- Hover effects
- Press animations
- Focus indicators
- Disabled states

**Don't override these** unless absolutely necessary.

### 5. Keep Custom Brushes Separate

If you need custom colors for specific use cases, define them in App.xaml but keep them separate from Material Design resources:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <!-- Material Design first -->
            <materialDesign:BundledTheme BaseTheme="Dark"/>
        </ResourceDictionary.MergedDictionaries>

        <!-- Then custom colors -->
        <SolidColorBrush x:Key="BackgroundDark" Color="#0D1117"/>
    </ResourceDictionary>
</Application.Resources>
```

---

## Common Patterns

### Pattern 1: Action Buttons

For action-oriented buttons (Start, Stop, Restart):

```xml
<Button Content="START"
        Style="{StaticResource MaterialDesignRaisedSecondaryButton}"
        Click="OnStartClicked"
        Width="80"/>
```

### Pattern 2: Icon Buttons

For toolbar or icon-only buttons:

```xml
<Button Style="{StaticResource MaterialDesignToolButton}"
        ToolTip="Refresh"
        Click="OnRefreshClicked">
    <materialDesign:PackIcon Kind="Refresh" Width="24" Height="24"/>
</Button>
```

### Pattern 3: Floating Action Button (FAB)

For primary screen action:

```xml
<Button Style="{StaticResource MaterialDesignFloatingActionButton}"
        Click="OnAddClicked"
        VerticalAlignment="Bottom"
        HorizontalAlignment="Right"
        Margin="20">
    <materialDesign:PackIcon Kind="Plus" Width="24" Height="24"/>
</Button>
```

---

## Resources

### Official Documentation
- **GitHub Wiki:** https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/wiki
- **Button Styles:** https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/wiki/Button-Styles
- **Color Swatches:** https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/wiki/Swatches-and-Recommended-Colors
- **Demo App:** Included with NuGet package - shows all controls

### Material Design Spec
- **Material Design Guidelines:** https://material.io/design
- **Components:** https://material.io/components

### Community
- **GitHub Discussions:** https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/discussions
- **Gitter Chat:** https://gitter.im/MaterialDesignInXAML/MaterialDesignInXamlToolkit

---

## Migration from Custom Styles

If you have existing WPF code with custom button styles, migrate like this:

### Before (Custom):
```xml
<Button Content="Submit"
        Background="#51CF66"
        Foreground="White"
        Padding="5"
        FontWeight="Bold"
        Width="100">
    <Button.Style>
        <Style TargetType="Button">
            <Style.Triggers>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.5"/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </Button.Style>
</Button>
```

### After (Material Design):
```xml
<Button Content="SUBMIT"
        Style="{StaticResource MaterialDesignRaisedSecondaryButton}"
        Width="100"/>
```

**Benefits:**
- 95% less XAML code
- Automatic theme support
- Proper disabled states
- Professional appearance
- Maintained by Material Design team

---

## Summary

### Key Takeaways

1. **Use Material Design styles** - Don't create custom button/control styles
2. **Follow conventions** - UPPERCASE buttons, proper spacing, Material Design patterns
3. **Let Material Design handle states** - Never write custom disabled/hover/press states
4. **Reference official docs** - Check wiki before implementing custom solutions
5. **Blame Google, not us** - If there's a design issue, it's Material Design's fault! 😄

---

**For questions or issues, check:**
- [Material Design GitHub Issues](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/issues)
- [Material Design Discussions](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/discussions)
