using Xunit;

// MewUI 的控件不是线程安全的（父子关系维护与继承属性刷新都假定单线程），
// xunit 默认按类并行，会让两个测试同时构造控件树而互相踩坏。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
