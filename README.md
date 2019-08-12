# MetaSpritePlus

This fork adds extra import capibilities to the excellent MetaSprite importer.

MetaSprite is an Unity plugin that lets you import [Aseprite][aseprite]'s .ase file into Unity, as Mecanim animation clips/controllers. It also has rich **metadata support** built in, allowing one to manipulate colliders, change transforms, send messages (and much more!) easily in Aseprite.

MetaSpritePlus adds a "data" importer. This lets you define Asesprite layers as a "data" layer.  Any pixels in each frame of that layer are saved to storage, which can then be accessed during gameplay.  For instance, you could define a characters footsteps and emit dust particles on specific frames of animation.

I also added code comments to help clarify what's happening.  This may help other developers add their own layer processing.

# Requirements

You'll need the [Serialized Dictionary Lite](https://assetstore.unity.com/packages/tools/utilities/serialized-dictionary-lite-110992) plugin by Rotary Heart.  This is used to store the layer data from the editor in a format that can be accessed during runtime.

# Installation

**As a Git Submodule**

If you want to install this in a way you can contribute to the source, do the following:
* Create a "Submodules" folder in your project's root folder.
* Clone this project into it: `git clone https://github.com/marcpeterson/MetaSpritePlus.git`
* Go into your `Assets` folder and make a symlink into the MetaSpritePlus "Editor" folder: 
`mklink /d /j MetaSpritePlus ..\Submodules\MetaSpritePlus\Assets\Plugins\MetaSprite\Editor`

Now you can edit the source as you use it in your project, yet still push changes to GitHub.

**The Easy Way**

If you're not interested in contributing to the source, then it's much easier to simply copy the `Assets/Plugins` folder into your Unity project's `Assets` folder.

See [wiki](https://github.com/marcpeterson/MetaSpritePlus/wiki) for explanation of importing, import settings, meta layers and other importer features.

# Roadmap

* Break layers into separate images: This will facilitate sprite swapping defined in the Aesprite file.  Say you have different arms animated.  Instead of baking them
  into a single sprite image, split it so you can turn each layer on/off at runtime.  SubLayers sorta do this, but can be improved.
* Sprite reuse in atlas: If the same image is already in a sprite sheet, reuse it instead of storing it again.  Will significantly compress animations
  that have paused or repeated images.

# Credits

* [WeAthFoLD](https://github.com/WeAthFoLD/MetaSprite/)'s original Aseprite importer
* [tommo](https://github.com/tommo)'s Aseprite importer in gii engine
* [talecrafter](https://github.com/talecrafter)'s [AnimationImporter](https://github.com/talecrafter/AnimationImporter), where the code of this project is initially based from

[aseprite]: https://aseprite.org

# Donation

Give all credit to WeAthFoLD for making the importer.  You can donate to him at:

<a href="https://www.patreon.com/bePatron?u=2955382">
<img src="https://c5.patreon.com/external/logo/become_a_patron_button.png"/>
</a>