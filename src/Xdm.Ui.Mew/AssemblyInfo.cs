using System.Runtime.CompilerServices;

// 桥接器、EditBox 等是视图层的内部实现，不对外暴露；测试工程需要直接观测它们。
[assembly: InternalsVisibleTo("Xdm.Ui.Mew.Tests")]
