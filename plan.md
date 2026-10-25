# 地形系统rework

最早的要求是要实现类似于OpenTTD的地形系统，但现在看上去差别很大

openttd虽然是一个2d游戏，但他地形本质上是3d的。我喜欢那种棱角分明的观感。

参考OpenTTD进行rework： /home/leo/dev/openttd/OpenTTD （已经开放权限）

地形本身观感类似于这样：/home/leo/dev/mech/plan_imgs/ottd.png

树木、道路、房子的效果类似于：/home/leo/dev/mech/plan_imgs/road_tree_building.png

* 按照格子来
* 一格内可以放置多个树木
* 道路可以做成这种对角的形式
* 目前我们的单位大小似乎合理
* 主战场模式的地图还是16km宽
* 楼房目前用方块代替
* 主基地的模型暂时不变
* 设想一个格子16米宽

# 改进 phase 1

1. 路面采用更类似OpenTTD的方式（看一下OpenTTD公路的实现）
   * 其实我就是想让路面贴格子 
   * OpenTTD的一个问题在于没有斜向45度的公路，我们需要一个特殊的实现，通过拼接tile来做到
2. 新的楼房不错，改进样式，增加窗户
3. 楼房的碰撞箱可能需要改进，目前似乎子弹会传入楼内再爆炸，导致看不到爆炸烟雾