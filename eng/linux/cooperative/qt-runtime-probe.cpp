// TypeLay optional Qt 6 bundle ABI check. No GUI, input context, or field access.
#include <QCoreApplication>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLibraryInfo>
#include <QPluginLoader>
#include <cstdio>

int main(int argc, char **argv) {
    QCoreApplication application(argc, argv);
    if (argc != 3) return 2;
    QPluginLoader provider(QString::fromLocal8Bit(argv[1]));
    QPluginLoader consumer(QString::fromLocal8Bit(argv[2]));
    const QJsonObject result{
        {QStringLiteral("buildVersion"), QStringLiteral(QT_VERSION_STR)},
        {QStringLiteral("runtimeVersion"), QString::fromLatin1(qVersion())},
        {QStringLiteral("systemPluginPath"), QLibraryInfo::path(QLibraryInfo::PluginsPath)},
        {QStringLiteral("provider"), provider.metaData()},
        {QStringLiteral("consumer"), consumer.metaData()}
    };
    const QByteArray output = QJsonDocument(result).toJson(QJsonDocument::Compact);
    std::puts(output.constData());
    return provider.metaData().isEmpty() || consumer.metaData().isEmpty() ? 1 : 0;
}
